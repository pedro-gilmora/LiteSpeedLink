using System.Buffers;
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

    // Canal ya conectado: se devuelve la llamada del canal sin maquina de estados propia (como EnumerateAsync).
    public ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "") =>
        Volatile.Read(ref _channel) is { } channel
            ? channel.GetAsync<TIn, TOut>(op, payload, true, token)
            : ConnectThenGetAsync<TIn, TOut>(op, payload, true, token);

    public ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, CancellationToken token = default, [CallerMemberName] string name = "") =>
        Volatile.Read(ref _channel) is { } channel
            ? channel.GetAsync<byte, TOut>(op, default, false, token)
            : ConnectThenGetAsync<byte, TOut>(op, default, false, token);

    public ValueTask<TOut> GetRawAsync<TState, TOut>(long op, TState state, Action<IBufferWriter<byte>, TState> write, RawReader<TOut> read, CancellationToken token = default) =>
        Volatile.Read(ref _channel) is { } channel
            ? channel.GetRawAsync(op, state, write, read, token)
            : ConnectThenGetRawAsync(op, state, write, read, token);

    public ValueTask SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>
        (long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "") =>
        Volatile.Read(ref _channel) is { } channel
            ? channel.SendAsync(op, payload, true, token)
            : ConnectThenSendAsync(op, payload, true, token);

    public ValueTask SendAsync(long op, CancellationToken token = default, [CallerMemberName] string name = "") =>
        Volatile.Read(ref _channel) is { } channel
            ? channel.SendAsync<byte>(op, default, false, token)
            : ConnectThenSendAsync<byte>(op, default, false, token);

    // Solo antes de conectar (una vez por conexion).
    private async ValueTask<TOut?> ConnectThenGetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, TIn? payload, bool hasPayload, CancellationToken token) =>
        await (await GetChannelAsync(token).ConfigureAwait(false)).GetAsync<TIn, TOut>(op, payload, hasPayload, token).ConfigureAwait(false);

    private async ValueTask<TOut> ConnectThenGetRawAsync<TState, TOut>(long op, TState state, Action<IBufferWriter<byte>, TState> write, RawReader<TOut> read, CancellationToken token) =>
        await (await GetChannelAsync(token).ConfigureAwait(false)).GetRawAsync(op, state, write, read, token).ConfigureAwait(false);

    private async ValueTask ConnectThenSendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(long op, TIn? payload, bool hasPayload, CancellationToken token) =>
        await (await GetChannelAsync(token).ConfigureAwait(false)).SendAsync(op, payload, hasPayload, token).ConfigureAwait(false);

    public IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "") =>
        Volatile.Read(ref _channel) is { } channel
            ? channel.EnumerateAsync<TIn, TOut>(op, payload, true, token)
            : ConnectThenEnumerateAsync<TIn, TOut>(op, payload, true, token);

    public IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, CancellationToken token = default, [CallerMemberName] string name = "") =>
        Volatile.Read(ref _channel) is { } channel
            ? channel.EnumerateAsync<byte, TOut>(op, default, false, token)
            : ConnectThenEnumerateAsync<byte, TOut>(op, default, false, token);

    /// <summary>Solo antes de conectar (una vez por conexion): despues el canal se devuelve directo, sin iterador intermedio.</summary>
    private async IAsyncEnumerable<TOut?> ConnectThenEnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, TIn? payload, bool hasPayload, [EnumeratorCancellation] CancellationToken token)
    {
        var channel = await GetChannelAsync(token).ConfigureAwait(false);
        await foreach (var item in channel.EnumerateAsync<TIn, TOut>(op, payload, hasPayload, token).ConfigureAwait(false))
            yield return item;
    }
}
