using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace SourceCrafter.LiteSpeedLink.Client;

/// <summary>
/// Base comun de los transportes de flujo (TCP, UDS) sobre <see cref="MultiplexedChannel"/>: conexion diferida unica y
/// reenvio de llamadas. No extensible fuera del ensamblado (constructor <c>private protected</c>).
/// </summary>
public abstract class StreamConnection : IAsyncConnection, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private Task<MultiplexedChannel>? _connecting;
    private MultiplexedChannel? _channel;
    private Stream? _stream;

    private readonly bool _coalesce;

    /// <param name="coalesceStreams">Por defecto: el lector agrupa los items de stream de cada lectura en una sola entrega (sin espera ni cambio de cable).</param>
    private protected StreamConnection(bool coalesceStreams = true) => _coalesce = coalesceStreams;

    /// <summary>Abre el flujo ya conectado (y autenticado, si aplica). <c>name</c> identifica al remoto en errores.</summary>
    private protected abstract Task<(Stream Stream, string Name)> OpenAsync();

    /// <summary>Cierra el recurso de transporte antes de liberar el canal.</summary>
    private protected abstract void Close();

    public async ValueTask DisposeAsync()
    {
        Close();
        if (_channel is not null) await _channel.DisposeAsync().ConfigureAwait(false);
        if (_stream is not null) await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private protected ValueTask<MultiplexedChannel> GetChannelAsync(CancellationToken token)
    {
        if (Volatile.Read(ref _channel) is { } ready) return new(ready);

        // Una sola conexion en curso; si fallo o se cancelo, el siguiente llamador reintenta.
        lock (_gate)
            if (_connecting is null or { IsFaulted: true } or { IsCanceled: true })
                _connecting = ConnectAsync();

        return new(_connecting.WaitAsync(token));
    }

    private async Task<MultiplexedChannel> ConnectAsync()
    {
        var (stream, name) = await OpenAsync().ConfigureAwait(false);
        _stream = stream;
        var channel = new MultiplexedChannel(PipeReader.Create(stream), PipeWriter.Create(stream), name, _coalesce);
        Volatile.Write(ref _channel, channel);
        return channel;
    }

    public async ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "") =>
        await (await GetChannelAsync(token).ConfigureAwait(false)).GetAsync<TIn, TOut>(op, payload, true, token).ConfigureAwait(false);

    public async ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, CancellationToken token = default, [CallerMemberName] string name = "") =>
        await (await GetChannelAsync(token).ConfigureAwait(false)).GetAsync<byte, TOut>(op, default, false, token).ConfigureAwait(false);

    public async ValueTask<ReadOnlyMemory<byte>> GetRawAsync(long op, ReadOnlyMemory<byte> request, CancellationToken token = default) =>
        await (await GetChannelAsync(token).ConfigureAwait(false)).GetRawAsync(op, request, token).ConfigureAwait(false);

    public async ValueTask SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>
        (long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "") =>
        await (await GetChannelAsync(token).ConfigureAwait(false)).SendAsync(op, payload, true, token).ConfigureAwait(false);

    public async ValueTask SendAsync(long op, CancellationToken token = default, [CallerMemberName] string name = "") =>
        await (await GetChannelAsync(token).ConfigureAwait(false)).SendAsync<byte>(op, default, false, token).ConfigureAwait(false);

    public async IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, TIn payload, [EnumeratorCancellation] CancellationToken token = default, [CallerMemberName] string name = "")
    {
        var channel = await GetChannelAsync(token).ConfigureAwait(false);
        await foreach (var item in channel.EnumerateAsync<TIn, TOut>(op, payload, true, token).ConfigureAwait(false))
            yield return item;
    }

    public async IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, [EnumeratorCancellation] CancellationToken token = default, [CallerMemberName] string name = "")
    {
        var channel = await GetChannelAsync(token).ConfigureAwait(false);
        await foreach (var item in channel.EnumerateAsync<byte, TOut>(op, default, false, token).ConfigureAwait(false))
            yield return item;
    }
}
