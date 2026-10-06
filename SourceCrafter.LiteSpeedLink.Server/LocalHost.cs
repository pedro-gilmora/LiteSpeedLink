namespace SourceCrafter.LiteSpeedLink;

/// <summary>Superficie <see cref="IAsyncDisposable"/> de un host <c>Local</c>, sea Memory o UDS.</summary>
public sealed class LocalHost(IDisposable inner) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        inner.Dispose();
        return default;
    }
}
