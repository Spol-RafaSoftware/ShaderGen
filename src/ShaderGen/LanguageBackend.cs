﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ShaderGen
{
    public abstract class LanguageBackend
    {
        protected readonly Compilation Compilation;

        internal class BackendContext
        {
            internal List<StructureDefinition> Structures { get; } = new List<StructureDefinition>();
            internal List<ResourceDefinition> Resources { get; } = new List<ResourceDefinition>();
            internal List<ShaderFunctionAndBlockSyntax> Functions { get; } = new List<ShaderFunctionAndBlockSyntax>();
        }

        internal Dictionary<string, BackendContext> Contexts = new Dictionary<string, BackendContext>();

        private readonly Dictionary<ShaderFunction, string> _fullTextShaders = new Dictionary<ShaderFunction, string>();

        internal LanguageBackend(Compilation compilation)
        {
            Compilation = compilation;
        }

        // Must be called before attempting to retrieve the context.
        internal void InitContext(string setName)
        {
            if (Contexts.ContainsKey(setName))
            {
                throw new InvalidOperationException("A set was initialized twice: " + setName);
            }

            Contexts.Add(setName, new BackendContext());
        }

        internal BackendContext GetContext(string setName)
        {
            if (!Contexts.TryGetValue(setName, out BackendContext ret))
            {
                throw new InvalidOperationException("There was no Shader Set generated with the name " + setName);
            }
            return ret;
        }


        internal ShaderModel GetShaderModel(string setName)
        {
            BackendContext context = GetContext(setName);

            foreach (ResourceDefinition rd in context.Resources
                .Where(rd => 
                    rd.ResourceKind == ShaderResourceKind.Uniform 
                    || rd.ResourceKind == ShaderResourceKind.RWStructuredBuffer
                    || rd.ResourceKind == ShaderResourceKind.StructuredBuffer))
            {
                ForceTypeDiscovery(setName, rd.ValueType);
            }
            // HACK: Discover all field structure types.
            foreach (StructureDefinition sd in context.Structures.ToArray())
            {
                foreach (FieldDefinition fd in sd.Fields)
                {
                    ForceTypeDiscovery(setName, fd.Type);
                }
            }

            // HACK: Discover all method input structures.
            foreach (ShaderFunctionAndBlockSyntax sf in context.Functions.ToArray())
            {
                if (sf.Function.IsEntryPoint)
                {
                    GetCode(setName, sf.Function);
                }
            }

            return new ShaderModel(
                context.Structures.ToArray(),
                context.Resources.ToArray(),
                context.Functions.Select(sfabs => sfabs.Function).ToArray());
        }

        private void ForceTypeDiscovery(string setName, TypeReference fd)
        {
            if (ShaderPrimitiveTypes.IsPrimitiveType(fd.Name))
            {
                return;
            }
            if (!TryDiscoverStructure(setName, fd.Name, out StructureDefinition sd))
            {
                throw new ShaderGenerationException("" +
                    "Resource type's field could not be resolved: " + fd.Name + " " + fd.Name);
            }
            foreach (FieldDefinition field in sd.Fields)
            {
                ForceTypeDiscovery(setName, field.Type);
            }
        }

        public string GetCode(string setName, ShaderFunction function)
        {
            if (function == null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (!_fullTextShaders.TryGetValue(function, out string result))
            {
                if (!function.IsEntryPoint)
                {
                    throw new ShaderGenerationException("Functions listed in a ShaderSet attribute must have either VertexFunction or FragmentFunction attributes.");
                }

                result = GenerateFullTextCore(setName, function);
                _fullTextShaders.Add(function, result);
            }

            return result;
        }

        internal string CSharpToShaderType(string fullType)
        {
            if (fullType == null)
            {
                throw new ArgumentNullException(nameof(fullType));
            }

            return CSharpToShaderTypeCore(fullType);
        }

        internal virtual void AddStructure(string setName, StructureDefinition sd)
        {
            if (sd == null)
            {
                throw new ArgumentNullException(nameof(sd));
            }

            List<StructureDefinition> structures = GetContext(setName).Structures;
            if (!structures.Any(old => old.Name == sd.Name))
            {
                structures.Add(sd);
            }
        }

        internal virtual bool IsIndexerAccess(SymbolInfo member)
        {
            return Utilities.GetFullMetadataName(member.Symbol.ContainingType) == "System.Numerics.Matrix4x4"
                && member.Symbol.Name[0] == 'M'
                && char.IsDigit(member.Symbol.Name[1]);
        }

        internal virtual void AddResource(string setName, ResourceDefinition rd)
        {
            if (rd == null)
            {
                throw new ArgumentNullException(nameof(rd));
            }

            GetContext(setName).Resources.Add(rd);
        }

        internal virtual void AddFunction(string setName, ShaderFunctionAndBlockSyntax sf)
        {
            if (sf == null)
            {
                throw new ArgumentNullException(nameof(sf));
            }

            GetContext(setName).Functions.Add(sf);
        }

        internal virtual string CSharpToShaderIdentifierName(SymbolInfo symbolInfo)
        {
            string typeName = symbolInfo.Symbol.ContainingType.ToDisplayString();
            string identifier = symbolInfo.Symbol.Name;

            return CorrectIdentifier(CSharpToIdentifierNameCore(typeName, identifier));
        }

        internal string FormatInvocation(string setName, string type, string method, InvocationParameterInfo[] parameterInfos)
        {
            Debug.Assert(setName != null);
            Debug.Assert(type != null);
            Debug.Assert(method != null);
            Debug.Assert(parameterInfos != null);

            ShaderFunctionAndBlockSyntax function = GetContext(setName).Functions
                .SingleOrDefault(sfabs => sfabs.Function.DeclaringType == type && sfabs.Function.Name == method);
            if (function != null)
            {
                string invocationList = string.Join(", ", parameterInfos.Select(ipi => CSharpToIdentifierNameCore(ipi.FullTypeName, ipi.Identifier)));
                string fullMethodName = CSharpToShaderType(function.Function.DeclaringType) + "_" + function.Function.Name;
                return $"{fullMethodName}({invocationList})";
            }
            else
            {
                return FormatInvocationCore(setName, type, method, parameterInfos);
            }
        }

        protected void ValidateRequiredSemantics(string setName, ShaderFunction function, ShaderFunctionType type)
        {
            if (type == ShaderFunctionType.VertexEntryPoint)
            {
                StructureDefinition outputType = GetRequiredStructureType(setName, function.ReturnType);
                foreach (FieldDefinition field in outputType.Fields)
                {
                    if (field.SemanticType == SemanticType.None)
                    {
                        throw new ShaderGenerationException("Function return type is missing semantics on field: " + field.Name);
                    }
                }
            }
            if (type != ShaderFunctionType.Normal)
            {
                foreach (ParameterDefinition pd in function.Parameters)
                {
                    StructureDefinition pType = GetRequiredStructureType(setName, pd.Type);
                    foreach (FieldDefinition field in pType.Fields)
                    {
                        if (field.SemanticType == SemanticType.None)
                        {
                            throw new ShaderGenerationException(
                                $"Function parameter {pd.Name}'s type is missing semantics on field: {field.Name}");
                        }
                    }
                }
            }
        }

        protected virtual StructureDefinition GetRequiredStructureType(string setName, TypeReference type)
        {
            StructureDefinition result = GetContext(setName).Structures.SingleOrDefault(sd => sd.Name == type.Name);
            if (result == null)
            {
                if (!TryDiscoverStructure(setName, type.Name, out result))
                {
                    throw new ShaderGenerationException("Type referred by was not discovered: " + type.Name);
                }
            }

            return result;
        }

        internal virtual string CorrectFieldAccess(SymbolInfo symbolInfo)
        {
            string mapped = CSharpToShaderIdentifierName(symbolInfo);
            return CorrectIdentifier(mapped);
        }

        protected bool TryDiscoverStructure(string setName, string name, out StructureDefinition sd)
        {
            INamedTypeSymbol type = Compilation.GetTypeByMetadataName(name);
            if (type == null)
            {
                throw new ShaderGenerationException("Unable to obtain compilation type metadata for " + name);
            }
            SyntaxNode declaringSyntax = type.OriginalDefinition.DeclaringSyntaxReferences[0].GetSyntax();
            if (declaringSyntax is StructDeclarationSyntax sds)
            {
                if (ShaderSyntaxWalker.TryGetStructDefinition(Compilation.GetSemanticModel(sds.SyntaxTree), sds, out sd))
                {
                    AddStructure(setName, sd);
                    return true;
                }
            }

            sd = null;
            return false;
        }

        internal abstract string CorrectIdentifier(string identifier);
        protected abstract string CSharpToShaderTypeCore(string fullType);
        protected abstract string CSharpToIdentifierNameCore(string typeName, string identifier);
        protected abstract string GenerateFullTextCore(string setName, ShaderFunction function);
        protected abstract string FormatInvocationCore(string setName, string type, string method, InvocationParameterInfo[] parameterInfos);
        internal abstract string GetComputeGroupCountsDeclaration(UInt3 groupCounts);

        internal string CorrectLiteral(string literal)
        {
            if (literal.EndsWith("f", StringComparison.OrdinalIgnoreCase))
            {
                if (!literal.Contains("."))
                {
                    // This isn't a hack at all
                    return literal.Insert(literal.Length - 1, ".");
                }
            }

            return literal;
        }
    }
}
