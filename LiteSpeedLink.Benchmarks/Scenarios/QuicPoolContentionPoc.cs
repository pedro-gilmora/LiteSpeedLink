using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;

namespace LiteSpeedLink.Benchmarks.Scenarios;

/// <summary>
/// PoC #14 (<c>--quicpool</c>): latencia de unarias pequenas mientras una unaria grande comparte la conexion.
/// La grande va por el pool multiplexado (op 2) o por stream propio de la MISMA conexion (op 3, via
/// EnumerateAsync de 1 item, que es exactamente la ruta dedicada). Mide media/p50/p99 de las pequenas.
/// </summary>
[SupportedOSPlatform("windows")]
#pragma warning disable CA2252
internal static class QuicPoolContentionPoc
{
    private const int Small = 1, LargePooled = 2, LargeOwn = 3;
    private const int Workers = 4, CallsPerWorker = 2000, LargeLoops = 2;

    public static async Task<int> RunAsync(int largeKb = 1024)
    {
        var big = new byte[largeKb * 1024];
        var cert = Constants.GetDevCert();
        await using var server = await Server.StartQuicServerAsync(0, (op, ctx, _) => op switch
        {
            Small => ctx.ReturnAsync(7),
            LargePooled => ctx.ReturnAsync(big),
            _ => ctx.EnumerateAsync(() => Enumerable.Repeat(big, 1))
        }, () => { }, cert);
        await using var conn = new DnsEndPoint("localhost", server.LocalEndPoint.Port).AsQuicConnection(cert);

        // Autochequeo: si la semantica falla, las cifras no significan nada.
        if (await conn.GetAsync<int, int>(Small, 0) != 7) return Fail("small");
        if ((await conn.GetAsync<int, byte[]>(LargePooled, 0))?.Length != big.Length) return Fail("large pooled");
        await foreach (var b in conn.EnumerateAsync<int, byte[]>(LargeOwn, 0))
            if (b?.Length != big.Length) return Fail("large own");

        Console.WriteLine($"Grande: {largeKb} KB x {LargeLoops} en bucle; pequenas: {Workers} workers x {CallsPerWorker}.");
        Console.WriteLine($"{"Escenario",-18}{"media us",10}{"p50 us",10}{"p99 us",10}{"max us",10}{"grandes/s",11}");

        for (int round = 0; round < 2; round++) // ronda 0 = calentamiento
        {
            await Measure(conn, "sin grande", null, round);
            await Measure(conn, "grande en pool", async ct => _ = await conn.GetAsync<int, byte[]>(LargePooled, 0, ct), round);
            await Measure(conn, "grande stream", async ct => { await foreach (var _ in conn.EnumerateAsync<int, byte[]>(LargeOwn, 0, ct)) { } }, round);
        }
        return 0;

        static int Fail(string what) { Console.WriteLine($"FALLO semantico: {what}"); return 1; }
    }

    private static async Task Measure(SourceCrafter.LiteSpeedLink.Client.QuicConnection conn, string name, Func<CancellationToken, Task>? large, int round)
    {
        using var stop = new CancellationTokenSource();
        long larges = 0;
        var bg = large is null ? [] : Enumerable.Range(0, LargeLoops).Select(_ => Task.Run(async () =>
        {
            try { while (!stop.IsCancellationRequested) { await large(stop.Token); Interlocked.Increment(ref larges); } }
            catch (OperationCanceledException) { }
        })).ToArray();

        await Task.Delay(50); // que las grandes esten en vuelo
        var lat = new long[Workers * CallsPerWorker];
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, Workers).Select(w => Task.Run(async () =>
        {
            for (int i = 0; i < CallsPerWorker; i++)
            {
                long t = Stopwatch.GetTimestamp();
                if (await conn.GetAsync<int, int>(Small, 0) != 7) throw new InvalidOperationException("small");
                lat[w * CallsPerWorker + i] = Stopwatch.GetTimestamp() - t;
            }
        })));
        var elapsed = sw.Elapsed.TotalSeconds;
        stop.Cancel();
        await Task.WhenAll(bg);
        if (round == 0) return;

        Array.Sort(lat);
        static double Us(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;
        Console.WriteLine($"{name,-18}{Us((long)lat.Average()),10:F1}{Us(lat[lat.Length / 2]),10:F1}{Us(lat[(int)(lat.Length * 0.99)]),10:F1}{Us(lat[^1]),10:F1}{larges / elapsed,11:F1}");
    }
}
