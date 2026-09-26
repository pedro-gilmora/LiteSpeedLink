using System.Buffers;

namespace SourceCrafter.LiteSpeedLink.Client;

/// <summary>Traduce un <see cref="ResponseStatus"/> distinto de exito a la excepcion que ve el llamador. Comun a todos los transportes.</summary>
internal static class ResponseError
{
    public static Exception Create(ResponseStatus status, string remote, ReadOnlySpan<byte> body) => status switch
    {
        ResponseStatus.NotFound => new NotImplementedException($"Implementation is missing from {remote}"),
        ResponseStatus.Failed => new InvalidOperationException($"Execution failed on {remote}:\nREASON:\n\n{Deserialize<string>(body)}"),
        _ => new InvalidDataException($"Unexpected response status {status} from {remote}.")
    };

    public static Exception Create(ResponseStatus status, string remote, in ReadOnlySequence<byte> body) => status switch
    {
        ResponseStatus.NotFound => new NotImplementedException($"Implementation is missing from {remote}"),
        ResponseStatus.Failed => new InvalidOperationException($"Execution failed on {remote}:\nREASON:\n\n{Deserialize<string>(body)}"),
        _ => new InvalidDataException($"Unexpected response status {status} from {remote}.")
    };
}
