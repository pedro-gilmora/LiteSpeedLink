using Application.Contracts;

using SourceCrafter.LiteSpeedLink;

using System.Diagnostics;
using System.Net.Quic;
using System.Runtime.Versioning;

// Memory: el único transporte con miembros sync nativos.
var rpcName = $"Test{Guid.CreateVersion7()}";
using (TextService.Start(rpcName))
{
    var auth = new TextServiceClient(rpcName).Auth;
    await Demo.RunAsync("Memory", auth, auth.GreetAsync);

    Console.WriteLine($"[Memory] TryAuth (async): {await auth.TryAuthAsync("pedro", "test!123")}");
    Console.WriteLine($"[Memory] Normalize (async): {await auth.NormalizeAsync("  hey  ")}");
    Console.WriteLine($"[Memory] Echo (async): {await auth.EchoAsync("  hi  ")}");
    Console.WriteLine($"[Memory] Square (server TOut): {await auth.SquareAsync("7")}");
    Console.WriteLine($"[Memory] Twice (client TOut): {auth.Twice(21) + 1}");
}

// Red: mismo contrato; los miembros sync son sync-over-async.
using (UdpTextService.Start(5101))
{
    var auth = new UdpTextServiceClient("localhost", 5101).Auth;
    await Demo.RunAsync("Udp", auth, auth.GreetAsync);
}

var tcpHost = TcpTextService.Start(5102, null);
try
{
    var auth = new TcpTextServiceClient("localhost", 5102).Auth;
    await Demo.RunAsync("Tcp", auth, auth.GreetAsync);
}
finally { tcpHost.Stop(); }

if (QuicListener.IsSupported)
{
    await using var quicHost = await QuicTextService.StartAsync(5103, Constants.GetDevCert());
    var auth = new QuicTextServiceClient("localhost", 5103).Auth;
    await Demo.RunAsync("Quic", auth, auth.GreetAsync);

    // [DedicatedStream]: 1 MB por su propio stream mientras las unarias pequeñas siguen en el pool.
    var download = auth.DownloadAsync(1 << 20);
    var greetings = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => auth.GreetAsync($"user{i}").AsTask()));
    Console.WriteLine($"[Quic] Download (dedicated stream): {(await download).Length} B, {greetings.Length} greets en paralelo");
}

// Streams: IEnumerable/IAsyncEnumerable con política de lotes por operación ([Stream(Batch, MaxDelayMs)]).
var streamHost = StreamService.Start(5104, null);
try
{
    var streams = new StreamServiceClient("localhost", 5104).Streams;
    await Demo.StreamsAsync("Tcp", streams);
}
finally { streamHost.Stop(); }

static class Demo
{
    public static async Task RunAsync(string transport, IAuth auth, Func<string, CancellationToken, ValueTask<string>> greetAsync)
    {
        var started = Stopwatch.GetTimestamp();
        void Log(string line) => Console.WriteLine($"[{transport}] {line}");

        try
        {
            // out
            Log($"TryAuth (out): {auth.TryAuth("pedro", "test!123", out var token)} {token}");

            // ref + out
            var counter = 41;
            auth.Bump(ref counter, out var label);
            Log($"Bump (ref + out): {counter} {label}");

            // Procesadores: in, ref + out, y la cadena completa cliente/servidor.
            Log($"Shout (in + processors): {auth.Shout("  hey  ")}");

            var text = "  hey  ";
            var result = auth.Normalize(ref text, out var length);
            Log($"Normalize (ref/out + processors): {result} {text} {length} {Check((result, text, length) == ("ok!", "<HEY>", 3))}");

            Log($"Greet (async): {await greetAsync("  Pedro  ", default)}");
            Log($"Echo (all processors): {auth.Echo("  hi  ")}");

            await auth.TouchAsync("pedro");
            Log("Touch (raw Task): ok");

            // Rechazos: cliente (excepción local) y servidor (early-return).
            try { auth.Echo("   "); }
            catch (PipelineRejectedException ex) { Log($"Rejected (client): {ex.Message}"); }

            try { await greetAsync("   ", default); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Pipeline 'TrimName' returned Failed.")) { Log("Rejected (server, early-return): ok"); }
        }
        catch (Exception ex) { Log(ex.ToString()); }
        finally { Log($"Took: {Stopwatch.GetElapsedTime(started)}"); }
    }

    public static async Task StreamsAsync(string transport, IStreams streams)
    {
        const int count = 1_000;
        var started = Stopwatch.GetTimestamp();
        void Log(string line) => Console.WriteLine($"[{transport}] {line}");

        try
        {
            (string Name, Func<int, IEnumerable<int>> Run)[] sync =
            [
                (nameof(IStreams.SyncDefault), streams.SyncDefault),
                (nameof(IStreams.SyncUnbatched), streams.SyncUnbatched),
                (nameof(IStreams.SyncBatched), streams.SyncBatched),
                (nameof(IStreams.SyncTimed), streams.SyncTimed),
            ];

            foreach (var (name, run) in sync)
                Log($"{name} (IEnumerable): {Check(run(count).SequenceEqual(Enumerable.Range(0, count)))}");

            (string Name, Func<int, IAsyncEnumerable<int>> Run)[] async =
            [
                (nameof(IStreams.AsyncDefault), streams.AsyncDefault),
                (nameof(IStreams.AsyncUnbatched), streams.AsyncUnbatched),
                (nameof(IStreams.AsyncBatched), streams.AsyncBatched),
                (nameof(IStreams.AsyncTimed), streams.AsyncTimed),
            ];

            foreach (var (name, run) in async)
            {
                var items = await run(count).ToListAsync();
                Log($"{name} (IAsyncEnumerable): {Check(items.SequenceEqual(Enumerable.Range(0, count)))}");
            }
        }
        catch (Exception ex) { Log(ex.ToString()); }
        finally { Log($"Took: {Stopwatch.GetElapsedTime(started)}"); }
    }

    static string Check(bool ok) => ok ? "ok" : "MISMATCH";
}

[SupportedOSPlatform("windows")]
[RequiresPreviewFeatures]
partial class Program;
