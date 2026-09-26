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
        int maxInFlightPerConnection = MaxInFlightPerConnection)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInFlightPerConnection, 1);

        File.Delete(path);

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen();

        ListenClientsAsync(listener, handlers, onFinalize, maxInFlightPerConnection, token);

        return listener;

        static async void ListenClientsAsync(Socket listener, RequestHandler handlers, Action onFinalize, int limit, CancellationToken token)
        {
            try
            {
                while (true)
                    _ = HandleConnectionAsync(await listener.AcceptAsync(token).ConfigureAwait(false), handlers, limit, token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
            finally
            {
                onFinalize();
            }
        }

        static async Task HandleConnectionAsync(Socket socket, RequestHandler handlers, int limit, CancellationToken token)
        {
            try
            {
                await using var stream = new NetworkStream(socket, ownsSocket: true);
                await ServePipeAsync(PipeReader.Create(stream), PipeWriter.Create(stream), handlers, token, limit).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
            }
        }
    }
}
