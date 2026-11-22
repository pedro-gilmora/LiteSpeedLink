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
        int maxConnections = MaxConnections,
        int idleTimeoutMs = IdleTimeoutMs)
        => StartUdsServer(path, new DelegateRequestHandler(handlers), onFinalize, token, maxInFlightPerConnection, maxConnections, idleTimeoutMs);

    public static Socket StartUdsServer<THandler>(
        string path,
        THandler handlers,
        Action onFinalize,
        CancellationToken token = default,
        int maxInFlightPerConnection = MaxInFlightPerConnection,
        int maxConnections = MaxConnections,
        int idleTimeoutMs = IdleTimeoutMs)
        where THandler : struct, IRequestHandler
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInFlightPerConnection, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnections, 1);

        File.Delete(path);

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));

        // Solo el usuario del host puede conectar (Linux exige escritura sobre el socket para connect).
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        listener.Listen();

        ListenClientsAsync(listener, handlers, onFinalize, maxInFlightPerConnection, idleTimeoutMs, new SemaphoreSlim(maxConnections), token);

        return listener;

        static async void ListenClientsAsync(Socket listener, THandler handlers,
            Action onFinalize, int limit, int idle, SemaphoreSlim connections, CancellationToken token)
        {
            try
            {
                while (true)
                {
                    await connections.WaitAsync(token).ConfigureAwait(false);

                    Socket socket;
                    try { socket = await listener.AcceptAsync(token).ConfigureAwait(false); }
                    catch { connections.Release(); throw; }

                    _ = HandleConnectionAsync(socket, handlers, limit, idle, connections, token);
                }
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
            THandler handlers, int limit, int idle, SemaphoreSlim connections, CancellationToken token)
        {
            try
            {
                await using var stream = new NetworkStream(socket, ownsSocket: true);
                await ServePipeAsync(PipeReader.Create(stream), PipeWriter.Create(stream), handlers, token, limit, idleTimeoutMs: idle).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
            }
            finally
            {
                connections.Release();
            }
        }
    }
}
