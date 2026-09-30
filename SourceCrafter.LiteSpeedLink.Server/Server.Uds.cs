using System.IO.Pipelines;
using System.Net.Sockets;

namespace SourceCrafter.LiteSpeedLink;

public static partial class Server
{
    /// <summary>Servidor sobre Unix Domain Socket: transporte local multiplataforma, mismo framing que TCP.</summary>
    public static Socket StartUdsServer(
        string path,
        RequestHandler handlers,
        Action onFinalize,
        CancellationToken token = default,
        int maxInFlightPerConnection = MaxInFlightPerConnection,
        int streamBatch = 0,
        TimeSpan streamBatchMaxDelay = default)
        => StartUdsServer(path, new DelegateRequestHandler(handlers), onFinalize, token, maxInFlightPerConnection, streamBatch, streamBatchMaxDelay);

    public static Socket StartUdsServer<THandler>(
        string path,
        THandler handlers,
        Action onFinalize,
        CancellationToken token = default,
        int maxInFlightPerConnection = MaxInFlightPerConnection,
        int streamBatch = 0,
        TimeSpan streamBatchMaxDelay = default)
        where THandler : struct, IRequestHandler
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInFlightPerConnection, 1);
        var batch = BatchPolicy.Create(streamBatch, streamBatchMaxDelay);

        File.Delete(path);

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen();

        ListenClientsAsync(listener, handlers, onFinalize, maxInFlightPerConnection, batch, token);

        return listener;

        static async void ListenClientsAsync(Socket listener, THandler handlers,
            Action onFinalize, int limit, BatchPolicy batch, CancellationToken token)
        {
            try
            {
                while (true)
                    _ = HandleConnectionAsync(await listener.AcceptAsync(token).ConfigureAwait(false), handlers, limit, batch, token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
            finally
            {
                onFinalize();
            }
        }

        static async Task HandleConnectionAsync(Socket socket,
            THandler handlers, int limit, BatchPolicy batch, CancellationToken token)
        {
            try
            {
                await using var stream = new NetworkStream(socket, ownsSocket: true);
                await ServePipeAsync(PipeReader.Create(stream), PipeWriter.Create(stream), handlers, token, limit, batch).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
            }
        }
    }
}
