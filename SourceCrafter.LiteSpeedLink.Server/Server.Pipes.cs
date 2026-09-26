using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace SourceCrafter.LiteSpeedLink;

public delegate ValueTask<ResponseStatus> RequestHandler(long id, RequestContext ctx, CancellationToken token);

public static partial class Server
{
    /// <summary>
    /// Atiende una conexion multiplexada: cada peticion <c>[len][corrId][opId][cuerpo]</c> se despacha
    /// en paralelo y su respuesta <c>[len][corrId][estado][cuerpo]</c> sale con el mismo corrId, de
    /// modo que el cliente la empareja aunque las respuestas lleguen desordenadas.
    /// </summary>
    internal static async Task ServePipeAsync(PipeReader reader, PipeWriter writer, RequestHandler handlers, CancellationToken token)
    {
        var responses = new ResponseChannel(writer);
        var inflight = new ConcurrentDictionary<Task, byte>();

        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(token).ConfigureAwait(false);
                var buffer = result.Buffer;

                while (Framing.TryReadFrame(ref buffer, out var correlationId, out var content))
                {
                    if (content.Length < Framing.OpIdSize) throw new InvalidDataException($"Request too short: {content.Length}.");

                    // El lector avanza antes de que el handler termine: el cuerpo se copia a un
                    // buffer alquilado que vive lo que dura la peticion.
                    long op = Framing.ReadOpId(content);
                    int length = (int)content.Length - Framing.OpIdSize;
                    byte[] body = ArrayPool<byte>.Shared.Rent(length);
                    content.Slice(Framing.OpIdSize).CopyTo(body);

                    var request = DispatchAsync(handlers, op, new(correlationId, body.AsMemory(0, length), responses, token), body, token);

                    if (!request.IsCompleted && inflight.TryAdd(request, 0))
                        _ = request.ContinueWith(static (t, s) => ((ConcurrentDictionary<Task, byte>)s!).TryRemove(t, out _), inflight, TaskScheduler.Default);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted || result.IsCanceled) break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException)
        {
        }
        finally
        {
            try { await Task.WhenAll(inflight.Keys).ConfigureAwait(false); } catch { }

            await reader.CompleteAsync().ConfigureAwait(false);
            await writer.CompleteAsync().ConfigureAwait(false);
        }
    }

    private static async Task DispatchAsync(RequestHandler handlers, long op, RequestContext ctx, byte[] body, CancellationToken token)
    {
        try
        {
            await handlers(op, ctx, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!ctx.IsCompleted)
        {
            try { await ctx.FailAsync(ex).ConfigureAwait(false); } catch { }
        }
        catch
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(body);
        }
    }
}

/// <summary>
/// Unico punto de escritura de una conexion: serializa el acceso al <see cref="PipeWriter"/> para
/// que las tramas de respuestas concurrentes nunca se intercalen.
/// </summary>
internal sealed class ResponseChannel(PipeWriter writer, bool correlated = true)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FrameWriter _frames = new(writer, correlated);

    public async ValueTask<ResponseStatus> WriteAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
        int correlationId, ResponseStatus status, T? body, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _frames.BeginFrame(correlationId, Framing.StatusSize)[0] = (byte)status;

            try
            {
                Serialize(_frames, body);
            }
            catch (Exception ex)
            {
                // La trama ya esta a medias en el writer y no se puede retirar: la conexion
                // queda inservible y se cierra para que el cliente no lea basura.
                await writer.CompleteAsync(ex).ConfigureAwait(false);
                throw;
            }

            _frames.EndFrame();

            // Sin token: cancelar un flush a medias partiria la trama.
            await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        return status;
    }

    public async ValueTask<ResponseStatus> WriteStatusAsync(int correlationId, ResponseStatus status, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _frames.BeginFrame(correlationId, Framing.StatusSize)[0] = (byte)status;
            _frames.EndFrame();

            await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        return status;
    }
}

/// <summary>
/// Peticion en curso sobre una conexion multiplexada. Todas las respuestas salen con el corrId de
/// la peticion y usan el token de cancelacion del servidor.
/// </summary>
public sealed class RequestContext
{
    private readonly int _correlationId;
    private readonly ReadOnlyMemory<byte> _body;
    private readonly ResponseChannel _responses;
    private readonly CancellationToken _token;
    private int _completed;

    internal RequestContext(int correlationId, ReadOnlyMemory<byte> body, ResponseChannel responses, CancellationToken token)
    {
        _correlationId = correlationId;
        _body = body;
        _responses = responses;
        _token = token;
    }

    /// <summary>Ya se envio la respuesta final (valor, fin de stream o error).</summary>
    internal bool IsCompleted => Volatile.Read(ref _completed) == 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TOut? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>() => Deserialize<TOut>(_body.Span);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<ResponseStatus> ReturnAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(TOut? payload)
    {
        Complete();
        return _responses.WriteAsync(_correlationId, ResponseStatus.Success, payload, _token);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<ResponseStatus> YieldAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(TData item) =>
        _responses.WriteAsync(_correlationId, ResponseStatus.Success, item, _token);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<ResponseStatus> EndStreamingAsync()
    {
        Complete();
        return _responses.WriteStatusAsync(_correlationId, ResponseStatus.StreamEnd, _token);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<ResponseStatus> NotFoundAsync()
    {
        Complete();
        return _responses.WriteStatusAsync(_correlationId, ResponseStatus.NotFound, _token);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<ResponseStatus> FailAsync(Exception exception)
    {
        Complete();
        return _responses.WriteAsync(_correlationId, ResponseStatus.Failed, exception.Message, _token);
    }

    public async ValueTask<ResponseStatus> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(Func<IAsyncEnumerable<TData>> value)
    {
        await foreach (var item in value().WithCancellation(_token).ConfigureAwait(false))
            await YieldAsync(item).ConfigureAwait(false);

        return await EndStreamingAsync().ConfigureAwait(false);
    }

    public async ValueTask<ResponseStatus> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(Func<IEnumerable<TData>> value)
    {
        foreach (var item in value())
            await YieldAsync(item).ConfigureAwait(false);

        return await EndStreamingAsync().ConfigureAwait(false);
    }

    private void Complete() => Volatile.Write(ref _completed, 1);
}
