namespace SourceCrafter.LiteSpeedLink;

public enum ResponseStatus : byte
{
    Success,
    NotFound,
    Failed,
    /// <summary>Cierra un stream de resultados; no lleva cuerpo.</summary>
    StreamEnd,
    /// <summary>Varios items de stream en una trama. Defecto de TCP/UDS/QUIC (lote del servidor) y agrupado del lector (<c>coalesceStreams</c>).</summary>
    Batch
}