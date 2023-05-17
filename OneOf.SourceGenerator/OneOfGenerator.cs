﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace OneOf.SourceGenerator
{
    [Generator]
    public class OneOfGenerator : IIncrementalGenerator
    {
        private const string AttributeName = "GenerateOneOfAttribute";
        private const string AttributeNamespace = "OneOf";

        private readonly string _attributeText = $@"// <auto-generated />
using System;

#pragma warning disable 1591

namespace {AttributeNamespace}
{{
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    internal sealed class {AttributeName} : Attribute
    {{
    }}
}}
";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterPostInitializationOutput(ctx => ctx.AddSource($"{AttributeName}.g.cs", _attributeText));
            
            var oneOfClasses = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (s, _) => IsSyntaxTargetForGeneration(s), 
                    transform: static (ctx, _) => GetSemanticTargetForGeneration(ctx))
                .Where(static m => m is not null)
                .Collect();
            
            context.RegisterSourceOutput(oneOfClasses, (context, symbols) => Execute(context, symbols));


            static bool IsSyntaxTargetForGeneration(SyntaxNode node)
            {
                return node is ClassDeclarationSyntax {AttributeLists.Count: > 0} classDeclarationSyntax
                       && classDeclarationSyntax.Modifiers.Any(SyntaxKind.PartialKeyword);
            };

            static INamedTypeSymbol? GetSemanticTargetForGeneration(GeneratorSyntaxContext context)
            {
                var symbol = context.SemanticModel.GetDeclaredSymbol(context.Node);

                if (symbol is not INamedTypeSymbol namedTypeSymbol)
                {
                    return null;
                }
                
                var attributeData = namedTypeSymbol.GetAttributes().FirstOrDefault(ad =>
                    string.Equals(ad.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), $"global::{AttributeNamespace}.{AttributeName}"));

                return attributeData is null ? null : namedTypeSymbol;
            }
        }

        private static string GenerateClassSource(INamedTypeSymbol classSymbol,
            ImmutableArray<ITypeParameterSymbol> typeParameters, ImmutableArray<ITypeSymbol> typeArguments)
        {
            IEnumerable<(ITypeParameterSymbol param, ITypeSymbol arg)> paramArgPairs =
                typeParameters.Zip(typeArguments, (param, arg) => (param, arg));

            string oneOfGenericPart = GetGenericPart(typeArguments);

            string classNameWithGenericTypes = $"{classSymbol.Name}{GetOpenGenericPart(classSymbol)}";

            StringBuilder source = new($@"// <auto-generated />
#pragma warning disable 1591

namespace {classSymbol.ContainingNamespace.ToDisplayString()}
{{
    partial class {classNameWithGenericTypes}");

            source.Append($@"
    {{
        public {classSymbol.Name}(OneOf.OneOf<{oneOfGenericPart}> _) : base(_) {{ }}
");

            foreach ((ITypeParameterSymbol param, ITypeSymbol arg) in paramArgPairs)
            {
                source.Append($@"
        public static implicit operator {classNameWithGenericTypes}({arg.ToDisplayString()} _) => new {classNameWithGenericTypes}(_);
        public static explicit operator {arg.ToDisplayString()}({classNameWithGenericTypes} _) => _.As{param.Name};
");
            }

            source.Append(@"    }
}");
            return source.ToString();
        }

        private static void Execute(SourceProductionContext context, ImmutableArray<INamedTypeSymbol?> symbols)
        {
            foreach (var namedTypeSymbol in symbols.Where(symbol => symbol is not null))
            {
                var classSource = ProcessClass(namedTypeSymbol!, context);
                
                if (classSource is null)
                {
                    continue;
                }

                context.AddSource($"{namedTypeSymbol!.ContainingNamespace}_{namedTypeSymbol.Name}.g.cs", classSource);
            }
        }

        private static string? ProcessClass(INamedTypeSymbol classSymbol, SourceProductionContext context)
        {
            var attributeLocation = classSymbol.Locations.FirstOrDefault() ?? Location.None;

            if (!classSymbol.ContainingSymbol.Equals(classSymbol.ContainingNamespace, SymbolEqualityComparer.Default))
            {
                CreateDiagnosticError(GeneratorDiagnosticDescriptors.TopLevelError);
                return null;
            }

            if (classSymbol.BaseType is null || classSymbol.BaseType.Name != "OneOfBase" || classSymbol.BaseType.ContainingNamespace.ToString() != "OneOf")
            {
                CreateDiagnosticError(GeneratorDiagnosticDescriptors.WrongBaseType);
                return null;
            }

            ImmutableArray<ITypeSymbol> typeArguments = classSymbol.BaseType.TypeArguments;

            foreach (ITypeSymbol typeSymbol in typeArguments)
            {
                if (typeSymbol.Name == nameof(Object))
                {
                    CreateDiagnosticError(GeneratorDiagnosticDescriptors.ObjectIsOneOfType);
                    return null;
                }

                if (typeSymbol.TypeKind == TypeKind.Interface)
                {
                    CreateDiagnosticError(GeneratorDiagnosticDescriptors.UserDefinedConversionsToOrFromAnInterfaceAreNotAllowed);
                    return null;
                }
            }

            return GenerateClassSource(classSymbol, classSymbol.BaseType.TypeParameters, typeArguments);

            void CreateDiagnosticError(DiagnosticDescriptor descriptor)
            {
                context.ReportDiagnostic(Diagnostic.Create(descriptor, attributeLocation, classSymbol.Name,
                    DiagnosticSeverity.Error));
            }
        }

        private static string GetGenericPart(ImmutableArray<ITypeSymbol> typeArguments) =>
            string.Join(", ", typeArguments.Select(x => x.ToDisplayString()));

        private static string? GetOpenGenericPart(INamedTypeSymbol classSymbol)
        {
            if (!classSymbol.TypeArguments.Any())
            {
                return null;
            }

            return $"<{GetGenericPart(classSymbol.TypeArguments)}>";
        }
    }
}
