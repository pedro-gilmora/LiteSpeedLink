using Microsoft.CodeAnalysis;

using SourceCrafter.Helpers;

using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;

using static SourceCrafter.Helpers.Extensions;

namespace SourceCrafter.MemLink.Helpers
{
    [Generator]
    public partial class ServiceHandlersGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            //var serviceHandlerTypes = context.SyntaxProvider
            //    .ForAttributeWithMetadataName("SourceCrafter.MemLink.ServiceHandlerAttribute",
            //        (node, a) => true,
            //        (t, c) => (INamedTypeSymbol)t.TargetSymbol).Collect();

            var serviceClientTypes = context.SyntaxProvider
                .ForAttributeWithMetadataName("SourceCrafter.MemLink.ServiceClientAttribute",
                    (node, a) => true,
                    (t, c) => (INamedTypeSymbol)t.TargetSymbol).Collect();

            var serviceHostType = context.SyntaxProvider
                .ForAttributeWithMetadataName("SourceCrafter.MemLink.ServiceHostAttribute",
                    (node, a) => true,
                    (t, c) => (INamedTypeSymbol)t.TargetSymbol).Collect();

            context.RegisterSourceOutput(context.CompilationProvider.Combine(serviceHostType.Combine(serviceClientTypes)), GenerateSource);
        }

        private void GenerateSource(SourceProductionContext context, (Compilation, (ImmutableArray<INamedTypeSymbol> ,ImmutableArray<INamedTypeSymbol>)) data)
        {
#if DEBUG_SG
            Debugger.Launch();
#endif
            var(compilation, (serviceHosts, serviceClients)) = data;

            MD5? md5 = null;

            if (CanGenerateService(context, serviceHosts))
            {
                GenerateServiceHost(context, compilation, serviceHosts[0], md5 = MD5.Create());
            }

            if (CanGenerateService(context, serviceClients))
            {
                GenerateServiceClient(context, compilation, serviceClients[0], md5 ??= MD5.Create());
            }

            md5?.Dispose();
        }

        static bool CanClientGenerateServiceMethod(SourceProductionContext context, ITypeSymbol serviceInterface, bool isTask, bool hasCancelToken)
        {
            if (!isTask || !hasCancelToken)
            {
                string typeName = serviceInterface.ToGlobalNamespaced();

                var rule = new DiagnosticDescriptor(
                    id: "LITSPLNK022",
                    title: $"{typeName} not suitable for generation",
                    messageFormat: "The method '{0}' should be async and pass a CancellationToken as a parameter",
                    category: "Usage",
                    defaultSeverity: DiagnosticSeverity.Error,
                    isEnabledByDefault: true,
                    description: $"In order to generate a proper implementation of a network call, the operation method {typeName} should be async passing a cancellation token."
                );

                Diagnostic diagnostic = Diagnostic.Create(rule, serviceInterface.Locations[0], typeName);

                context.ReportDiagnostic(diagnostic);

                return false;
            }
            return true;
        }
        

        static bool CanGenerateService(SourceProductionContext context, ImmutableArray<INamedTypeSymbol> foundTypes)
        {
            if (foundTypes.Length > 1)
            {
                INamedTypeSymbol namedTypeSymbol = foundTypes[1];

                string typeName = namedTypeSymbol.ToGlobalNamespaced(),
                    simpleName = namedTypeSymbol.Name.Replace("Attribute", "");

                var rule = new DiagnosticDescriptor(
                    id: $"LITSPLNK001",
                    title: typeName,
                    messageFormat: "The attribute '{0}' should not be used more than once inside this project.",
                    category: "Usage",
                    defaultSeverity: DiagnosticSeverity.Error,
                    isEnabledByDefault: true,
                    description: $"There should be just one implementation of {simpleName}."
                );

                Diagnostic diagnostic = Diagnostic.Create(rule, namedTypeSymbol.Locations[0], typeName);

                context.ReportDiagnostic(diagnostic);

                return false;
            }
            return foundTypes.Length == 1;
        }

        static void GetMetaInfo(ISymbol symbol, out string nsMetaName, out string fullTypeMetaName, out string symbolMetaName, out string fullSymbolMetaName)
        {
            nsMetaName = symbol.ContainingNamespace.ToMetadataLongName();

            fullTypeMetaName = symbol.ContainingType.ToMetadataLongName();

            fullSymbolMetaName = symbol.ToMetadataLongName();

            symbolMetaName = nsMetaName.Length > 0
                ? fullSymbolMetaName.Replace(nsMetaName, "")
                : fullSymbolMetaName;
        }
    }
}

