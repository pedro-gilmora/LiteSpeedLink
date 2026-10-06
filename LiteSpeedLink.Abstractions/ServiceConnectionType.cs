namespace SourceCrafter.LiteSpeedLink
{
    public enum ServiceConnectionType
    {
        Memory,
        Udp,
        Tcp,
        Quic,
        /// <summary>Lo decide el generador segun el destino: Memory en Windows, UDS en el resto.</summary>
        Local
    }
}