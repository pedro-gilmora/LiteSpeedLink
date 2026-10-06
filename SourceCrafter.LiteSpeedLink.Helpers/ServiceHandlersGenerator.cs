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
/// Es la base comun de <c>ServiceHostGenerator</c> (ServerGenerator) y <c>ServiceClientGenerator</c>
/// (ClientGenerator, con <c>CLIENT_PARTIAL</c>): el registro de DI desduplica por nombre de tipo, asi que
/// cada ensamblado necesita el suyo.
/// </para>
/// </summary>
public abstract partial class ServiceHandlersGenerator : ServiceProviderPartial
{
    private const string ServiceHostAttr = "LiteSpeedLink.Abstractions.Internals.ServiceHostAttribute";
    private const string ServiceClientAttr = "LiteSpeedLink.Abstractions.Internals.ServiceClientAttribute";
    private const string ServiceUnitAttr = "LiteSpeedLink.Abstractions.Internals.ServiceUnitAttribute<TService>";
    private const string cancelTokenFullTypeName = "global::System.Threading.CancellationToken";

    /// <summary>
    /// Identificador estable de una operación. Debe producir el mismo valor en cliente y host,
    /// independientemente de la máquina, la cultura o el codepage ANSI por defecto.
    /// </summary>
    public static long GetServiceId(string input)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;

        var hash = offsetBasis;

        foreach (var b in System.Text.Encoding.UTF8.GetBytes(input))
        {
            hash = (hash ^ b) * prime;
        }

        return unchecked((long)hash);
    }

    static bool Exchange(ref bool value)
    {
        return ((value, _) = (true, value)).Item2;
    }

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

#if CLIENT_PARTIAL

            if (TryGetConnectionType(container.ContainerType, ServiceClientAttr, out var clientConnection))
            {
                var isLocal = ResolveLocal(container, ref clientConnection);
                GenerateServiceClient(container, contribution, clientConnection, isLocal, cancelToken);
            }
#else
            if (TryGetConnectionType(container.ContainerType, ServiceHostAttr, out var hostConnection))
            {
                var isLocal = ResolveLocal(container, ref hostConnection);
                GenerateServiceHost(container, contribution, hostConnection, isLocal, cancelToken);
            }
#endif
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

    /// <summary><c>ServiceConnectionType.Local</c>.</summary>
    private const int LocalConnection = 4;

    /// <summary>Transporte efectivo de <c>Local</c> fuera de Windows; no es un valor publico del enum.</summary>
    private const int UdsConnection = 5;

    /// <summary>Ruta del socket UDS de <c>Local</c>; host y cliente la derivan igual del mismo nombre.</summary>
    private const string LocalUdsPath = "global::System.IO.Path.Combine(global::System.IO.Path.GetTempPath(), name + \".lsl\")";

    /// <summary>
    /// <c>[SupportedOSPlatform]</c>/<c>[RequiresPreviewFeatures]</c> del transporte efectivo, emitidos en la clase generada para que el
    /// usuario no tenga que anotar su contenedor.
    /// </summary>
    private static string PlatformAttributes(int connectionType) => connectionType switch
    {
        0 => @"
[global::System.Runtime.Versioning.SupportedOSPlatform(""windows"")]",
        3 => @"
[global::System.Runtime.Versioning.SupportedOSPlatform(""windows"")]
[global::System.Runtime.Versioning.SupportedOSPlatform(""linux"")]
[global::System.Runtime.Versioning.SupportedOSPlatform(""macos"")]
[global::System.Runtime.Versioning.RequiresPreviewFeatures]",
        _ => ""
    };

    /// <summary>
    /// <c>Local</c> se decide aqui, en compilacion: Memory (RpcBuffer) si el destino es Windows, UDS en otro caso.
    /// El destino sale de <c>RuntimeIdentifier</c> o de un <c>TargetFramework</c> con plataforma; si las
    /// opciones globales no lo fijan, del SO donde compila (<see cref="System.Runtime.InteropServices.RuntimeInformation"/>).
    /// </summary>
    private static bool ResolveLocal(ServiceProviderInfo container, ref int connectionType)
    {
        if (connectionType != LocalConnection) return false;

        connectionType = TargetsWindows(container.GlobalOptions) ? 0 : UdsConnection;
        return true;
    }

    internal static bool TargetsWindows(Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions options)
    {
        if (options.TryGetValue("build_property.RuntimeIdentifier", out var rid) && rid.Length > 0)
            return rid.StartsWith("win", StringComparison.OrdinalIgnoreCase);

        if (options.TryGetValue("build_property.TargetFramework", out var tfm) && tfm.IndexOf('-') >= 0)
            return tfm.Contains("-windows", StringComparison.OrdinalIgnoreCase);

        return System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
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
