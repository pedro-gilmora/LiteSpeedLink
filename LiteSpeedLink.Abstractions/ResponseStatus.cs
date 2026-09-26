namespace SourceCrafter.LiteSpeedLink;

public enum ResponseStatus : byte
{
    Success,
    NotFound,
    Failed,
    /// <summary>Cierra un stream de resultados; no lleva cuerpo.</summary>
    StreamEnd
}