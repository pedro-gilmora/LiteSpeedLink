using SourceCrafter.MemLink;

const string host = "127.0.0.1";
const int port = 11000;

ServiceListener.Run(host, port);

await Task.Delay(500);
await Task.WhenAny(RunEchoClient(host, port), RunEchoClient(host, port));
await Task.Delay(TimeSpan.FromMinutes(5));
static async Task RunEchoClient(string host, int port)
{
    // Create KCP Client	
    await using var connection = await ServiceClient.ConnectAsync(host, port);
    try
    {
        Console.WriteLine($"Connected to {host}:{port}");

        (int, int)[] dataSet = [(1, 5), (7, 4), (3, 6), (8, 0), (4, 9), (1, 5), (7, 4), (3, 6), (8, 0), (4, 9), (1, 5), (7, 4), (3, 6), (8, 0), (4, 9), (1, 5), (7, 4), (3, 6), (8, 0), (4, 9)];

        int i = 1;

        foreach (var (a, b) in dataSet)
        {
            var ii = i++;
            await connection.SendAsync(($"Request #{ii}", a, b, i % 2 == 0));

            Console.WriteLine($@"[Client {connection.Connection.ConnectionId}] 
	Request #{ii}: a = {a}, b = {b}");

            var result = await connection.ReceiveAsync<int>();

            Console.WriteLine($@"[Server Response client {connection.Connection.ConnectionId}] 
	Response to request #{ii}: {result}");
        }
        await Task.Delay(1500);
    }
    catch (Exception e)
    {
        Console.WriteLine($"Client Exception: {e}");
    }
    finally
    {
        await connection.DisposeAsync();
    }
}
