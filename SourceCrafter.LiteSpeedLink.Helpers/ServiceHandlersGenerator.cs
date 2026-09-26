using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SourceCrafter.DependencyInjection.Generation;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

#pragma warning disable CA1050
/// <summary>
/// Generador parcial de LiteSpeedLink.
///
/// <para>
/// Antes era un <c>IIncrementalGenerator</c> propio que volvia a parsear los atributos de
/// registro del contenedor para reconstruir la tabla de servicios. Eso duplicaba las reglas
/// del generador de DI -nombres de miembro, forma del miembro, inlinado, disposability- y
/// quedaba desincronizado en cuanto una de ellas cambiaba. Ahora el generador de DI entrega
/// esa tabla ya resuelta, con los simbolos vivos, y aqui solo queda emitir.
/// </para>
///
/// <para>
/// Se descubre por el nombre del ensamblado, que debe empezar por
/// <c>SourceCrafter.DependencyInjection.Partial</c> (ver <c>AssemblyName</c> en el csproj).
/// </para>
/// </summary>
public sealed partial class ServiceHandlersGenerator : ServiceProviderPartial
{
    private const string ServiceHostAttr = "LiteSpeedLink.Abstractions.Internals.ServiceHostAttribute";
    private const string ServiceClientAttr = "LiteSpeedLink.Abstractions.Internals.ServiceClientAttribute";
    private const string ClientServiceAttr = "LiteSpeedLink.Abstractions.Internals.ClientServiceAttribute<TService>";

    public override void AnalyzeContainer(
        ServiceProviderInfo container,
        PartialContribution contribution,
        CancellationToken cancelToken)
    {
#if DEBUG_SG
        Debugger.Launch();
#endif
        try
        {
            // El contenedor es el propio host o cliente: el atributo se lee de su simbolo, no
            // de un SyntaxProvider aparte. Esto tambien elimina la comprobacion de unicidad
            // (LITSPLNK001): un contenedor no puede declararse dos veces.
            if (TryGetConnectionType(container.ContainerType, ServiceHostAttr, out var hostConnection))
            {
                GenerateServiceHost(container, contribution, hostConnection, cancelToken);
            }

            if (TryGetConnectionType(container.ContainerType, ServiceClientAttr, out var clientConnection))
            {
                GenerateServiceClient(container, contribution, clientConnection, cancelToken);
            }
        }
        catch (Exception e)
        {
            contribution.ReportDiagnostic(
                Diagnostic.Create(new DiagnosticDescriptor(
                    id: "SCLSL000",
                    "An error has ocurred while generating endpoints",
                    "Unexpected exception trying to build the containers",
                    "SourceCrafter.Generation",
                    DiagnosticSeverity.Error,
                    true,
                    e.ToString()), null));
        }
    }

    private static bool TryGetConnectionType(
        INamedTypeSymbol containerType,
        string attributeMetadataName,
        out int connectionType)
    {
        foreach (var attr in containerType.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() != attributeMetadataName) continue;

            connectionType = (int?)attr.ConstructorArguments.FirstOrDefault().Value ?? 0;
            return true;
        }

        connectionType = 0;
        return false;
    }

    /// <summary>
    /// Indexa los servicios por <c>(lifetime, tipo expuesto, clave)</c>, que es como los
    /// referencian los parametros anotados de un metodo de servicio.
    /// </summary>
    private static Dictionary<(PartialLifetime, string, string), ServiceInfo> IndexServices(
        ServiceProviderInfo container)
    {
        Dictionary<(PartialLifetime, string, string), ServiceInfo> map = [];

        foreach (var service in container.Services)
        {
            map[(service.Lifetime, service.ExportTypeFullName, service.Key)] = service;
        }

        return map;
    }
}
