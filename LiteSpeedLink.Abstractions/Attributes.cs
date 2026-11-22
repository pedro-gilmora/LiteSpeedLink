using System;

namespace SourceCrafter.LiteSpeedLink;


#pragma warning disable CS9113 // Parameter is unread.
/// <summary>TLS del transporte, fijado en compilacion: el generador emite la firma y la validacion exactas.</summary>
public enum TlsMode
{
    /// <summary>Tcp: TLS mutuo si se pasa certificado, texto plano si no. Quic: <see cref="Mutual"/>.</summary>
    Default,
    /// <summary>Sin TLS, sin parametro de certificado (no valido en Quic).</summary>
    None,
    /// <summary>Solo el host presenta certificado; el cliente lo valida.</summary>
    Server,
    /// <summary>Ambos extremos presentan y validan certificado.</summary>
    Mutual
}

/// <summary>Como se valida el certificado remoto; fijado en compilacion.</summary>
public enum CertificateCheck
{
    /// <summary>Cadena valida o exactamente el certificado configurado (SHA-256).</summary>
    ChainOrPinned,
    /// <summary>Solo cadena valida (CA de confianza).</summary>
    Chain,
    /// <summary>Solo el certificado configurado (SHA-256); ignora la cadena.</summary>
    Pinned
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ServiceHostAttribute(ServiceConnectionType connectionType = ServiceConnectionType.Memory) : Attribute
{
    public TlsMode Tls { get; set; }
    public CertificateCheck Check { get; set; }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ServiceClientAttribute(ServiceConnectionType connectionType = ServiceConnectionType.Memory) : Attribute
{
    public TlsMode Tls { get; set; }
    public CertificateCheck Check { get; set; }
}

/// <summary>Servicio expuesto por un [ServiceClient] como propiedad (IAuth -> Auth).</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ServiceUnitAttribute<TService> : Attribute where TService : IServiceUnit;

[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class ServiceAttribute(string? name = null) : Attribute;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ServiceHandlerAttribute : Attribute;

/// <summary>
/// Tamano maximo del cuerpo de una peticion de esta operacion, fijado en compilacion (por defecto 4 MB, como gRPC). Host y cliente generados
/// lo aplican antes de recibir o enviar el cuerpo. Para operaciones que lo necesitan (p.ej. subidas); el tope del protocolo son 16 MB.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class MaxMessageSizeAttribute(int bytes) : Attribute;

//[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
//internal sealed class PipelineAttribute<TPipeline>(SourceCrafter.DependencyInjection.Constants.Lifetime lifetime = SourceCrafter.DependencyInjection.Constants.Lifetime.Scoped, string nameOrFormat = "GetPipeline{0}") : Attribute where TPipeline : IPipeline;

//[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
//internal sealed class AsyncPipelineAttribute<TPipeline>(SourceCrafter.DependencyInjection.Constants.Lifetime lifetime = SourceCrafter.DependencyInjection.Constants.Lifetime.Scoped, string nameOrFormat = "GetPipeline{0}Async") : Attribute where TPipeline : IPipelineAsync;
#pragma warning restore CS9113 // Parameter is unread.