using Microsoft.CodeAnalysis;
using SourceCrafter.LiteSpeedLink.Helpers;



//using SourceCrafter.Helpers;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Xml.Schema;

//using static SourceCrafter.Helpers.Extensions;


[Generator(LanguageNames.CSharp)]
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
                int compilationId = compilation.GetHashCode();
                try
                {
                    if (CanGenerateService(context, serviceHosts))
                    {
                        GenerateServiceHost(context, compilation, compilationId, serviceHosts[0], context.CancellationToken);
                    }

                    if (CanGenerateService(context, serviceClients))
                    {
                        GenerateServiceClient(context, compilation, compilationId, serviceClients[0], context.CancellationToken);
                    }
                }
                catch (Exception e)
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(new DiagnosticDescriptor(
                            id: "SCLSL000",
                            "An error has ocurred while generating endpoints",
                            "Unexpected exception trying to build the containers",
                            "SourceCrafter.Generation",
                            DiagnosticSeverity.Error,
                            true,
                            e.ToString()), null));
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

        string typeName = cls.GlobalNamespaced,
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