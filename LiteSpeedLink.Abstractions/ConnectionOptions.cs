namespace SourceCrafter.LiteSpeedLink;

/// <summary>Endpoint TCP/UDP/QUIC (<c>JsonSetting&lt;RemoteOptions&gt;</c>); el transporte lo fija el atributo del contenedor.</summary>
public sealed class RemoteOptions : BaseOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 9876;
}

/// <summary>Endpoint Memory/UDS/Local (<c>JsonSetting&lt;LocalOptions&gt;</c>): nombre del canal; UDS deriva la ruta del socket de el.</summary>
public sealed class LocalOptions : BaseOptions
{
    public string Name { get; set; } = "lsl";
}

public abstract class BaseOptions
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
}

///// <summary>Endpoint QUIC enlazable desde configuracion (<c>JsonSetting&lt;QuicOptions&gt;</c>).</summary>
//public sealed class QuicOptions
//{
//    public string Host { get; set; } = "localhost";
//    public int Port { get; set; }
//}

///// <summary>Endpoint TCP enlazable desde configuracion (<c>JsonSetting&lt;TcpOptions&gt;</c>).</summary>
//public sealed class TcpOptions
//{
//    public string Host { get; set; } = "localhost";
//    public int Port { get; set; }
//}

///// <summary>Endpoint UDP enlazable desde configuracion (<c>JsonSetting&lt;UdpOptions&gt;</c>).</summary>
//public sealed class UdpOptions
//{
//    public string Host { get; set; } = "localhost";
//    public int Port { get; set; }
//}

///// <summary>Canal de memoria compartida enlazable desde configuracion (<c>JsonSetting&lt;MemoryOptions&gt;</c>).</summary>
//public sealed class MemoryOptions
//{
//    public string Name { get; set; } = "";
//}

///// <summary>Socket UDS enlazable desde configuracion (<c>JsonSetting&lt;UdsOptions&gt;</c>).</summary>
//public sealed class UdsOptions
//{
//    public string Path { get; set; } = "";
//}