using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SourceCrafter.DependencyInjection.Generation;
using SourceCrafter.LiteSpeedLink.Helpers;

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
    private const string ServiceHostAttr = "SourceCrafter.LiteSpeedLink.ServiceHostAttribute";
    private const string ServiceClientAttr = "SourceCrafter.LiteSpeedLink.ServiceClientAttribute";
    private const string ServiceUnitAttr = "SourceCrafter.LiteSpeedLink.ServiceUnitAttribute<TService>";
    private const string HostedAttr = "SourceCrafter.DependencyInjection.Attributes.HostedAttribute<T>";
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

            if (TryGetConnectionType(container.ContainerType, ServiceClientAttr, out var clientConnection, out var tls, out var check))
            {
                var isLocal = ResolveLocal(container, ref clientConnection);
                if (clientConnection == 3 && tls == TlsNone)
                    contribution.ReportDiagnostic(Diagnostic.Create(QuicWithoutTls, container.ContainerType.Locations.FirstOrDefault(), container.ContainerType.Name));
                else
                    GenerateServiceClient(container, contribution, clientConnection, isLocal, tls, check, cancelToken);
            }
#else
            if (TryGetConnectionType(container.ContainerType, ServiceHostAttr, out var hostConnection, out var tls, out var check))
            {
                var isLocal = ResolveLocal(container, ref hostConnection);
                if (hostConnection == 3 && tls == TlsNone)
                    contribution.ReportDiagnostic(Diagnostic.Create(QuicWithoutTls, container.ContainerType.Locations.FirstOrDefault(), container.ContainerType.Name));
                else
                    GenerateServiceHost(container, contribution, hostConnection, isLocal, tls, check, cancelToken);
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

    /// <summary>Ruta del socket UDS de <c>Local</c>; host y cliente la derivan igual del mismo nombre (directorio privado del usuario).</summary>
    private const string LocalUdsPath = "global::SourceCrafter.LiteSpeedLink.LocalSocket.GetPath(name)";

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

    private const string JsonSettingAttr = "SourceCrafter.DependencyInjection.MsConfiguration.Metadata.JsonSettingAttribute<T>";
    private const string JsonConfigAttr = "SourceCrafter.DependencyInjection.MsConfiguration.Metadata.JsonConfigurationAttribute";

    /// <summary>
    /// Miembro que MsConfiguration genera en el contenedor para <c>[JsonSetting&lt;RemoteOptions|LocalOptions&gt;]</c>,
    /// o <c>null</c> si no lo emitira (sin setting, sin <c>JsonConfiguration</c> con su <c>configKey</c> o sin
    /// <c>Microsoft.Extensions.Configuration</c>). Nombre con las mismas reglas: <c>nameFormat</c> con <c>key</c> en Pascal.
    /// </summary>
    private static string? EndpointSettings(ServiceProviderInfo container, bool remote)
    {
        if (container.Compilation.GetTypeByMetadataName("Microsoft.Extensions.Configuration.IConfiguration") is null) return null;

        var type = container.ContainerType;
        var optionsType = remote ? "SourceCrafter.LiteSpeedLink.RemoteOptions" : "SourceCrafter.LiteSpeedLink.LocalOptions";

        foreach (var attr in type.GetAttributes())
        {
            if (attr.AttributeClass is not { IsGenericType: true } ac
                || ac.ConstructedFrom.ToDisplayString() != JsonSettingAttr
                || ac.TypeArguments[0].ToDisplayString() != optionsType
                || attr.ConstructorArguments is not [{ Value: string { Length: > 0 } }, _, { Value: var key }, { Value: string nameFormat }, { Value: var configKey }, ..])
                continue;

            var cfgKey = (configKey?.ToString() ?? "").Trim();

            return type.ContainingAssembly.GetAttributes().Concat(type.GetAttributes()).Any(a =>
                    a.AttributeClass?.ToDisplayString() == JsonConfigAttr
                    && a.ConstructorArguments is [{ Value: string { Length: > 0 } }, { Value: var k }, ..]
                    && (k?.ToString() ?? "").Trim() == cfgKey)
                ? nameFormat.Replace("{0}", (key?.ToString() ?? "").Pascalize()).RemoveDuplicates()
                : null;
        }

        return null;
    }

    private static bool TryGetConnectionType(
        INamedTypeSymbol containerType,
        string attributeMetadataName,
        out int connectionType,
        out int tls,
        out int check)
    {
        foreach (var attr in containerType.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() != attributeMetadataName) continue;

            connectionType = (int?)attr.ConstructorArguments.FirstOrDefault().Value ?? 0;
            (tls, check) = (0, 0);

            foreach (var arg in attr.NamedArguments)
            {
                if (arg.Key == "Tls" && arg.Value.Value is int t) tls = t;
                else if (arg.Key == "Check" && arg.Value.Value is int c) check = c;
            }

            return true;
        }

        (connectionType, tls, check) = (0, 0, 0);
        return false;
    }

    // Espejo de SourceCrafter.LiteSpeedLink.TlsMode/CertificateCheck (Abstractions): el generador no la referencia.
    private const int TlsDefault = 0, TlsNone = 1, TlsServer = 2, CheckChain = 1;
    private static readonly string[] TlsModeNames = ["Default", "None", "Server", "Mutual"];
    private static readonly string[] CheckNames = ["ChainOrPinned", "Chain", "Pinned"];

    /// <summary>
    /// Firma del certificado fijada en compilacion: <c>null</c> = sin parametro; <c>"?"</c> = opcional (Tcp por defecto: sin el, texto plano);
    /// <c>""</c> = obligatorio. Solo cuenta para Tcp (2) y Quic (3).
    /// </summary>
    private static string? CertificateNullability(int connectionType, int tls, int check, bool client)
    {
        if (connectionType is not (2 or 3) || tls == TlsNone) return null;

        // Cliente que solo valida la cadena del host: no presenta certificado ni necesita pin.
        if (client && tls == TlsServer && check == CheckChain) return null;

        return connectionType == 2 && tls == TlsDefault ? "?" : "";
    }

    private static readonly DiagnosticDescriptor QuicWithoutTls = new(
        "SCLSL022",
        "QUIC always uses TLS",
        "'{0}' uses QUIC with Tls = TlsMode.None: QUIC always encrypts, use Server or Mutual",
        "SourceCrafter.LiteSpeedLink",
        DiagnosticSeverity.Error,
        true);

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
