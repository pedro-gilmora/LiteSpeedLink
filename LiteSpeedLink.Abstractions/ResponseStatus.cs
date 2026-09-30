namespace SourceCrafter.LiteSpeedLink;

public enum ResponseStatus : byte
{
    Success,
    NotFound,
    Failed,
    /// <summary>Cierra un stream de resultados; no lleva cuerpo.</summary>
    StreamEnd,
    /// <summary>Varios items de stream en una trama: <c>[len:int32][item]...</c>. Opt-in (<c>streamBatch</c> / <c>coalesceStreams</c>).</summary>
    Batch
}