using System.Diagnostics;
using System.Net;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;

namespace LiteSpeedLink.Benchmarks;

/// <summary>
/// <c>--leak</c>: repite cada escenario por rondas y, tras GC completo, imprime heap retenido, handles del proceso y
/// conexiones dispuestas aun vivas (WeakReference). Retenido estable tras las primeras rondas = pools/capacidad; creciente = fuga.
/// </summary>
#pragma warning disable CA2252 // QUIC es preview
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class LeakProbe
{
    public static async Task<int> RunAsync(string? only, int Rounds = 10)
    {
        string name = $"Leak-{Guid.CreateVersion7():N}";
        RequestHandler handler = async (op, ctx, _) =>
        {
            int n = ctx.Get<int>();
            if (op == 1) return await ctx.EnumerateAsync(() => Enumerable.Range(0, n));
            if (op == 2) await Task.Delay(20);
            return await ctx.ReturnAsync(n + 1);
        };

        using var mem = Server.StartMemoryServerAsync(name, (op, ctx, _) => new(ctx.Return(ctx.Get<int>() + 1)), () => { });
        string path = Path.Combine(Path.GetTempPath(), name + ".sock");
        using var uds = Server.StartUdsServer(path, handler, () => { });
        var tcp = Server.StartTcpServer(0, handler, () => { });
        var tcpEp = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)tcp.LocalEndpoint).Port);
        var cert = Constants.GetDevCert();
        await using var quic = await Server.StartQuicServerAsync(0, handler, () => { }, cert);
        var quicEp = new DnsEndPoint("localhost", quic.LocalEndPoint.Port);

        await using var shared = new UdsConnection(path);
        var cache = new CacheManager<int, string>(1, 1024);
        int key = 0;

        (string Name, Func<List<WeakReference>, Task> Round)[] scenarios =
        [
            ("uds-churn", refs => Churn(refs, 200, () => new UdsConnection(path))),
            ("tcp-churn", refs => Churn(refs, 200, () => new TcpConnection(tcpEp))),
            ("quic-churn", refs => Churn(refs, 50, () => quicEp.AsQuicConnection(cert))),
            ("memory-churn", async refs =>
            {
                for (int i = 0; i < 50; i++)
                {
                    var c = new MemoryConnection(name, 5000);
                    for (int k = 0; k < 10; k++) await c.GetAsync<int, int>(0, k);
                    c.Dispose();
                    refs.Add(new(c));
                }
            }),
            ("uds-unary", _ => Parallel(64, 20_000 / 64, i => shared.GetAsync<int, int>(0, i).AsTask())),
            ("uds-cancel", _ => Parallel(64, 2_000 / 64, async i =>
            {
                using var cts = new CancellationTokenSource(1);
                try { await shared.GetAsync<int, int>(2, i, cts.Token); } catch (OperationCanceledException) { }
            })),
            ("uds-stream-break", _ => Parallel(16, 2_000 / 16, async i =>
            {
                await foreach (var x in shared.EnumerateAsync<int, int>(1, 1000)) if (x == 3) break;
            })),
            ("uds-stream-cancel", _ => Parallel(16, 1_000 / 16, async i =>
            {
                using var cts = new CancellationTokenSource();
                try { await foreach (var x in shared.EnumerateAsync<int, int>(1, 100_000, cts.Token)) if (x == 10) cts.Cancel(); }
                catch (OperationCanceledException) { }
            })),
            ("client-cache", _ =>
            {
                for (int i = 0; i < 100_000; i++)
                {
                    int k = key++;
                    cache.Set(k, "v");
                    if (cache.TryLead(-k - 1, out var flight)) cache.Fail(-k - 1, flight, null);
                }
                return Task.CompletedTask;
            }),
        ];

        Console.WriteLine("escenario: retenido KB por ronda | handles | conexiones dispuestas vivas");
        foreach (var (scenario, round) in scenarios)
        {
            if (only is not null && scenario != only) continue;

            var refs = new List<WeakReference>();
            int alive = 0, total = 0;
            var kb = new long[Rounds];
            var handles = new int[Rounds];
            for (int r = 0; r < Rounds; r++)
            {
                await round(refs);
                await Task.Delay(100);
                kb[r] = Retained() / 1024;
                // Se cuentan y descartan por ronda: la lista no debe contaminar la medida.
                (alive, total) = (alive + refs.Count(w => w.IsAlive), total + refs.Count);
                refs.Clear();
                refs.TrimExcess();
                using var p = Process.GetCurrentProcess();
                handles[r] = p.HandleCount;
                // LSL_LEAK_DUMP=dir: volcado de heap en la ronda 3 y en la ultima para diff con dotnet-dump (dumpheap -stat).
                if (Environment.GetEnvironmentVariable("LSL_LEAK_DUMP") is { } dir && (r == 2 || r == Rounds - 1))
                    Process.Start("dotnet-dump", $"collect -p {p.Id} --type Heap -o \"{Path.Combine(dir, $"{scenario}-{r}.dmp")}\"")!.WaitForExit();
            }

            // Pendiente entre la ronda 3 y la ultima: las primeras llenan pools y capacidades.
            double slope = (kb[^1] - kb[2]) / (double)(Rounds - 3);
            Console.WriteLine($"{scenario}: [{string.Join(", ", kb)}] KB, {slope:+0.0;-0.0} KB/ronda | " +
                $"handles [{handles[2]}..{handles[^1]}] | vivas {alive}/{total}");
        }

        tcp.Stop();
        return 0;
    }

    private static long Retained()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
        }
        return GC.GetTotalMemory(true);
    }

    private static async Task Churn<T>(List<WeakReference> refs, int count, Func<T> create) where T : IAsyncConnection, IAsyncDisposable
    {
        for (int i = 0; i < count; i++)
        {
            var c = create();
            for (int k = 0; k < 10; k++) await c.GetAsync<int, int>(0, k);
            await c.DisposeAsync();
            refs.Add(new(c));
        }
    }

    private static Task Parallel(int c, int each, Func<int, Task> call) =>
        Task.WhenAll(Enumerable.Range(0, c).Select(async w =>
        {
            for (int i = 0; i < each; i++) await call(w * each + i);
        }));
}
