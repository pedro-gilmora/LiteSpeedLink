using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using LiteSpeedLink.Benchmarks.Scenarios;

namespace LiteSpeedLink.Benchmarks;

/// <summary>
/// <c>--alloc</c>: muestrea por tipo (GCAllocationTick, ~100 KB por muestra) las asignaciones de todo el proceso
/// mientras se drena un stream grande de Memory. Sirve para saber QUE asigna, no cuanto.
/// </summary>
internal sealed class AllocProbe : EventListener
{
    private readonly ConcurrentDictionary<string, long> _bytes = new();

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1 /* GC */);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName?.StartsWith("GCAllocationTick") != true || e.Payload is null) return;

        int t = e.PayloadNames!.IndexOf("TypeName"), a = e.PayloadNames.IndexOf("AllocationAmount64");
        if (t < 0) return;
        long amount = a >= 0 ? Convert.ToInt64(e.Payload[a]) : 100_000;
        _bytes.AddOrUpdate((string)e.Payload[t]!, amount, (_, v) => v + amount);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static async Task<int> RunQuicAsync()
    {
        var bench = new QuicOverheadBenchmarks();
        await bench.Setup();
        const int calls = 5000;

        var excs = new ConcurrentDictionary<string, int>();
        AppDomain.CurrentDomain.FirstChanceException += (_, e) => excs.AddOrUpdate($"{e.Exception.GetType().Name}: {e.Exception.Message} @ {e.Exception.TargetSite?.DeclaringType?.Name}.{e.Exception.TargetSite?.Name}", 1, (_, v) => v + 1);
        using var probe = new AllocProbe();
        long before = GC.GetTotalAllocatedBytes(true);
        for (int i = 0; i < calls; i++) await bench.Lsl();
        long total = GC.GetTotalAllocatedBytes(true) - before;
        foreach (var (k, v) in excs) Console.WriteLine($"EXC x{v}: {k}");

        Console.WriteLine($"Total: {total / 1_000_000.0:F1} MB ({total / (double)calls:F0} B/llamada)");
        foreach (var (type, bytes) in probe._bytes.OrderByDescending(kv => kv.Value).Take(20))
            Console.WriteLine($"{bytes * 100.0 / total,6:F1}%  {type}");

        await bench.Cleanup();
        return 0;
    }

    public static async Task<int> RunAsync(string transport = "memory")
    {
        var bench = new StreamingBenchmarks { Items = 1 };
        await bench.Setup();
        Func<Task<int>> run = transport switch
        {
            "tcp" => bench.Tcp,
            "uds" => bench.Uds,
            "quic" => bench.Quic,
            _ => bench.Memory
        };

        foreach (var (items, reps) in new[] { (1, 5000), (100_000, 5) })
        {
            bench.Items = items;
            await run();
            using var probe = new AllocProbe();
            long before = GC.GetTotalAllocatedBytes(true);
            for (int i = 0; i < reps; i++) await run();
            long total = GC.GetTotalAllocatedBytes(true) - before;

            Console.WriteLine($"[{transport}] items={items}: {total / (double)reps:F0} B/llamada ({total / (double)(reps * items):F1} B/item)");
            foreach (var (type, bytes) in probe._bytes.OrderByDescending(kv => kv.Value).Take(10))
                Console.WriteLine($"{bytes * 100.0 / total,6:F1}%  {type}");
        }

        await bench.Cleanup();
        return 0;
    }

    public static async Task<int> RunStreamAsync()
    {
        var bench = new StreamingBenchmarks { Items = 1_000_000 };
        await bench.Setup();

        using var probe = new AllocProbe();
        long before = GC.GetTotalAllocatedBytes(true);
        await bench.Memory();
        long total = GC.GetTotalAllocatedBytes(true) - before;

        Console.WriteLine($"Total: {total / 1_000_000.0:F1} MB ({total / (double)bench.Items:F1} B/item)");
        foreach (var (type, bytes) in probe._bytes.OrderByDescending(kv => kv.Value).Take(12))
            Console.WriteLine($"{bytes * 100.0 / total,6:F1}%  {type}");

        await bench.Cleanup();
        return 0;
    }
}
