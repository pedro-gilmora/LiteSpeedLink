using KcpTransport;

using System.Collections.Concurrent;
using System.Net;

namespace SourceCrafter.MemLink;

public class ServiceClient
{
    readonly static ConcurrentDictionary<string, KcpPooledConnection> connectionsPool = new();
    readonly static SemaphoreSlim poolSemaphore = new(1);

    public static async ValueTask<KcpPooledConnection> ConnectAsync(string host, int port, KcpClientConnectionOptions? opts = null)
    {
        await poolSemaphore.WaitAsync();

        string key = string.Intern($"{host}:{port}");

        if (connectionsPool.TryGetValue(key, out var current))
        {
            if (!current.Disconnected)
            {
                //Console.WriteLine($"Reusing client connection: {current.Connection.ConnectionId}");
                current.LastTimeCheck = DateTime.UtcNow;

                poolSemaphore.Release();

                return current;
            }
            else
            {
                //Console.WriteLine($"Removing client connection: {current.Connection.ConnectionId}");
                await TryRemove(key);
            }
        }

        opts ??= new() { RemoteEndPoint = IPEndPoint.Parse(key) };

        var connection = await KcpConnection.ConnectAsync(host, port);

        current = new(key, connection, DateTime.UtcNow);

        await current.InitAsync();

        connectionsPool[key] = current;

        ConnectionIdleCheck(current, opts.ConnectionTimeout);

        connectionsPool[key] = current;

        poolSemaphore.Release();

        return current;

        void ConnectionIdleCheck(KcpPooledConnection entry, TimeSpan connectionTimeout)
        {
            CancellationTokenSource cts = new();

            Timer timer = null!;

            timer = new Timer(async state =>
            {
                entry.CancellationSource.Token.ThrowIfCancellationRequested();

                if (DateTime.UtcNow - entry.LastTimeCheck > connectionTimeout)
                {
                    if (entry.Disconnected)
                    {
                        //Console.WriteLine($"Disposed client connection: {current.Connection.ConnectionId}");
                        await TryRemove(entry.Key);
                        entry.CancellationSource.Cancel();
                    }
                    else
                    {
                        entry.LastTimeCheck = DateTime.UtcNow;
                    }
                }
            }, null, connectionTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    static async ValueTask TryRemove(string key)
    {
        if (!connectionsPool.TryRemove(key, out var entry)) return;

        var id = entry.Connection.ConnectionId;

        try
        {
            entry.Disconnected = true;
            await entry.DisposeAsync();
            entry = null;
        }
        finally
        {
            Console.WriteLine($"Client removed connection {id}");
        }
    }
}