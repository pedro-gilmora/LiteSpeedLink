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

    public static async Task<int> RunAsync()
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
