using SourceCrafter.LiteSpeedLink;

using System.Diagnostics;

const string host = "127.0.0.1";
const int port = 11000;

TextService.ListenAsync(host, port);
await Task.Delay(500);
do
{
    await Task.WhenAny(RunEchoClient(host, port), RunEchoClient(host, port));
}
while (Console.ReadKey().KeyChar == '+');



static async Task RunEchoClient(string host, int port)
{
    var start = Stopwatch.GetTimestamp();
    // Create KCP Client	
    var (connection, stream) = await ServiceClient.ConnectAsync(host, port);
    using (connection)
    using (stream)
    try
    {
        //Console.WriteLine($"Connected to {host}:{port}");

        (int, int)[] dataSet = [(1, 5), (7, 4), (3, 6), (8, 0), (4, 9), (1, 5), (7, 4), (3, 6), (8, 0), (4, 9), (1, 5), (7, 4), (3, 6), (8, 0), (4, 9), (1, 5), (7, 4), (3, 6), (8, 0), (4, 9)];

        int i = 1;

        foreach (var (a, b) in dataSet)
        {
            var ii = i++;
            var response = await stream.GetAsync("97584cff868e41b7a8c3a92209e48151", (int[])[a, b]);

 //           Console.WriteLine($@"[Client {connection.ConnectionId}] 
	//Request #{ii}: a = {a}, b = {b}");

            var result = response.Get<int>();

 //           Console.WriteLine($@"[Server Response client {connection.ConnectionId}] 
	//Response to request #{ii}: {result}");
        }
    }
    catch (Exception e)
    {
        //Console.WriteLine($"Client Exception: {e}");
    }
    finally
    {
        Console.WriteLine($"Took {Stopwatch.GetElapsedTime(start)}");
        connection.Dispose();
    }
}
