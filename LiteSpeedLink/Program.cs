//Console.WriteLine("Test");
using Application.Contracts;

using SourceCrafter.LiteSpeedLink;
using System.Diagnostics;
using System.Runtime.Versioning;


var rpcName = $"Test{Guid.CreateVersion7()}";

using var server = TextService.Start(rpcName);

TextServiceClient client = new (rpcName);

Console.WriteLine("Hola!");
var timestamp = Stopwatch.GetTimestamp();
try
{
    //Debugger.Launch();
    if (client.Auth.TryAuth("pedro", "test!123", out var token))
    {
        Console.WriteLine(token);
    }
    Console.WriteLine($"TryAuth (async): {await client.Auth.TryAuthAsync("pedro", "test!123")}");
    int memCounter = 41;
    client.Auth.Bump(ref memCounter, out var memLabel);
    Console.WriteLine($"Bump (ref+out): {memCounter} {memLabel}");
    Console.WriteLine($"Sent a greeting: {await client.Auth.GreetAsync("  Pedro  ")}");
    Console.WriteLine($"Echo (all processors): {await client.Auth.EchoAsync("  hi  ")}");
    Console.WriteLine($"Echo (sync): {client.Auth.Echo("  hi  ")}");
    Console.WriteLine($"Square (server TOut): {await client.Auth.SquareAsync("7")}");
    Console.WriteLine($"Twice (client TOut): {client.Auth.Twice(21) + 1}");
    await client.Auth.TouchAsync("pedro");
    Console.WriteLine("Touch (raw Task): ok");
    try { client.Auth.Echo("   "); }
    catch (PipelineRejectedException ex) { Console.WriteLine($"Rejected: {ex.Message}"); }
}
catch (System.Exception ex)
{
    Console.WriteLine(ex.ToString());
}
finally
{
    Console.WriteLine($"Took: ${Stopwatch.GetElapsedTime(timestamp)}");
}

// Transportes de red: mismo contrato; los miembros sync son sync-over-async (solo Memory es sync nativo).
using (UdpTextService.Start(5101))
{
    var udp = new UdpTextServiceClient("localhost", 5101).Auth;
    await Exercise("Udp", udp, n => udp.GreetAsync(n));
}

var tcpHost = TcpTextService.Start(5102, null);
try
{
    var tcp = new TcpTextServiceClient("localhost", 5102).Auth;
    await Exercise("Tcp", tcp, n => tcp.GreetAsync(n));
}
finally { tcpHost.Stop(); }

if (System.Net.Quic.QuicListener.IsSupported)
{
    await using var quicHost = await QuicTextService.StartAsync(5103, Constants.GetDevCert());
    var quic = new QuicTextServiceClient("localhost", 5103).Auth;
    await Exercise("Quic", quic, n => quic.GreetAsync(n));
}

static async Task Exercise(string transport, IAuth auth, Func<string, ValueTask<string>> greetAsync)
{
    var ts = Stopwatch.GetTimestamp();
    try
    {
        Console.WriteLine($"[{transport}] TryAuth (sync): {auth.TryAuth("pedro", "test!123", out var token)} {token}");
        Console.WriteLine($"[{transport}] Greet (async): {await greetAsync("  Pedro  ")}");
        Console.WriteLine($"[{transport}] Echo (sync): {auth.Echo("  hi  ")}");
        await auth.TouchAsync("pedro");
        Console.WriteLine($"[{transport}] Touch (raw Task): ok");
        int counter = 41;
        auth.Bump(ref counter, out var label);
        Console.WriteLine($"[{transport}] Bump (ref+out): {counter} {label}");
        try { auth.Echo("   "); }
        catch (PipelineRejectedException ex) { Console.WriteLine($"[{transport}] Rejected: {ex.Message}"); }
    }
    catch (Exception ex) { Console.WriteLine($"[{transport}] {ex}"); }
    finally { Console.WriteLine($"[{transport}] Took: {Stopwatch.GetElapsedTime(ts)}"); }
}

[SupportedOSPlatform("windows")]
[System.Runtime.Versioning.RequiresPreviewFeatures]
partial class Program;