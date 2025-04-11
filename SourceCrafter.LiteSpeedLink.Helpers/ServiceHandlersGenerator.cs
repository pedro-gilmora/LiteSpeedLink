using Microsoft.CodeAnalysis;

using SourceCrafter.Helpers;


//using SourceCrafter.Helpers;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Xml.Schema;

//using static SourceCrafter.Helpers.Extensions;

namespace SourceCrafter.LiteSpeedLink
{
    [Generator]
    public partial class ServiceHandlersGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
#if DEBUG_SG
            Debugger.Launch();
#endif
            //var serviceHandlerTypes = context.SyntaxProvider
            //    .ForAttributeWithMetadataName("SourceCrafter.LiteSpeedLink.ServiceHandlerAttribute",
            //        (node, a) => true,
            //        (t, c) => (INamedTypeSymbol)t.TargetSymbol).Collect();

            var serviceClientTypes = context.SyntaxProvider
                .ForAttributeWithMetadataName("LiteSpeedLink.Abstractions.Internals.ServiceClientAttribute",
                    (node, a) => true,
                    (t, c) => ((INamedTypeSymbol)t.TargetSymbol, GetServiceConnectionType(t.Attributes))).Collect();

            var serviceHostType = context.SyntaxProvider
                .ForAttributeWithMetadataName("LiteSpeedLink.Abstractions.Internals.ServiceHostAttribute",
                    (node, a) => true,
                    (t, c) => ((INamedTypeSymbol)t.TargetSymbol, GetServiceConnectionType(t.Attributes)))
                .Collect();

            context.RegisterSourceOutput(context.CompilationProvider.Combine(serviceHostType.Combine(serviceClientTypes)),
                (context, info) =>
                {
                    var (compilation, (serviceHosts, serviceClients)) = info;
                    try
                    {

                        if (CanGenerateService(context, serviceHosts))
                        {
                            GenerateServiceHost(context, compilation, serviceHosts[0]);
                        }

                        if (CanGenerateService(context, serviceClients))
                        {
                            GenerateServiceClient(context, compilation, serviceClients[0]);
                        }
                    }
                    catch (Exception e)
                    {
                        context.AddSource("errors.cs", $"/*{e}*/");
                    }
                });
        }

        private int GetServiceConnectionType(ImmutableArray<AttributeData> attributes)
        {
            return (int?)attributes[0].ConstructorArguments.FirstOrDefault().Value ?? 0;
        }


        static bool CanGenerateService(SourceProductionContext context, in ImmutableArray<(INamedTypeSymbol, int)> classes)
        {
            if (classes.Length is 0) return false;

            if (classes is not ([_, (var cls, _), ..])) return true;

            string typeName = cls.ToGlobalNamespaced(),
                    simpleName = cls.Name.Replace("Attribute", "");

            var rule = new DiagnosticDescriptor(
                id: "LITSPLNK001",
                title: typeName,
                messageFormat: "The attribute '{0}' should not be used more than once inside this project.",
                category: "Usage",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                description: $"There should be just one implementation of {simpleName}."
            );

            Diagnostic diagnostic = Diagnostic.Create(rule, cls.Locations[0], typeName);

            context.ReportDiagnostic(diagnostic);

            return false;
        }
    }
}

namespace SourceCrafter
{
    public static class Extensions
    {
        const SymbolDisplayParameterOptions paramsOptions =
            SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeName |
            SymbolDisplayParameterOptions.IncludeDefaultValue;

        public static string GetString(this IParameterSymbol symbol)
        {
            return symbol.ToDisplayString(Helpers.Extensions._globalizedNamespace.WithParameterOptions(paramsOptions));
        }
    }
}