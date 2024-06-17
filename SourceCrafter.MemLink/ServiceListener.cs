using KcpTransport;

using System.Net;

namespace SourceCrafter.MemLink;

public class ServiceListener
{
    public static async void Run(string host, int port, CancellationToken token = default)
    {
        using var listener = await KcpListener.ListenAsync(host, port, token);

        Console.WriteLine($"Listening on {host}:{port}");

        while (true)
        {
            ConsumeClient(await listener.AcceptConnectionAsync(token), token);
        }
    }

    private static async void ConsumeClient(KcpConnection connection, CancellationToken token)
    {
        Console.WriteLine($"Server listenig on , Id:{connection.ConnectionId}");
        using (connection)
        await using (var stream = new MemlinkStream(await connection.OpenOutboundStreamAsync()))
            try
            {
                do
                {
                    var (i, a, b, isOdd) = await stream.ReadAsync<(string, int, int, bool)>();

                    Console.WriteLine($@"[Server processing client {connection.ConnectionId}] 
	{i} 
		Received: a = {a}, b = {b}, isOdd = {isOdd}
		Response: {a + b}");

                    await stream.WriteAsync(a + b);
                }
                while (true);
            }
            catch (KcpDisconnectedException)
            {
                // when client has been disconnected, ReadAsync will throw KcpDisconnectedException
                Console.WriteLine($"Server removed client connection #{connection.ConnectionId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($@"Server removed client connection #{connection.ConnectionId}: 

{ex}");
            }
            finally
            {
                Console.WriteLine($"Server ended connection with client #{connection.ConnectionId}");
            }
    }
}