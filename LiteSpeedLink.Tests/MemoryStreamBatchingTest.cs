using System.Diagnostics;
using System.Runtime.Versioning;

using FluentAssertions;

using SourceCrafter.LiteSpeedLink.Client;
using SourceCrafter.LiteSpeedLink;

namespace LiteSpeedLink.Tests;

/// <summary>Limites del streaming en memoria por lotes (POC A/B) y del watchdog de inactividad.</summary>
[SupportedOSPlatform("windows")]
public class MemoryStreamBatchingTest
{
    static ValueTask<ResponseStatus> Handle(long op, MemoryRequestContext ctx, CancellationToken token) => op switch
    {
        1 => ValueTask.FromResult(ctx.Yield(Enumerable.Range(0, ctx.Get<int>()))),
        3 => ValueTask.FromResult(ctx.Yield(Enumerable.Range(1, 3).Select(i => Enumerable.Repeat((byte)i, 100_000).ToArray()))),
        4 => ValueTask.FromResult(ctx.Yield(Enumerable.Range(ctx.Get<int>() * 1000, 500))),
        5 => ctx.Yield(Stall()),
        _ => ValueTask.FromResult(ctx.NotFound())
    };

    static async IAsyncEnumerable<int> Stall()
    {
        yield return 1;
        await Task.Delay(3000);
    }

    static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> items)
    {
        List<T> list = [];
        await foreach (var x in items) list.Add(x);
        return list;
    }

    static (IDisposable, MemoryConnection) Open(int timeout = 5000)
    {
        string name = $"Test-{Guid.CreateVersion7()}";
        return (Server.StartMemoryServerAsync(name, Handle, () => { }), new MemoryConnection(name, timeout));
    }

    [Fact]
    public async Task StreamSpanningManyBatchesKeepsOrderAndCount()
    {
        var (server, client) = Open();
        using (server) using (client)
        {
            // 20000 x [len+int] ~ 160 KB: cruza el umbral de 16 KB muchas veces.
            (await Drain(client.EnumerateAsync<int, int>(1, 20_000))).Should().Equal(Enumerable.Range(0, 20_000));
        }
    }

    [Fact]
    public async Task ItemsLargerThanANodeArriveIntact()
    {
        var (server, client) = Open();
        using (server) using (client)
        {
            // 100 KB por item > nodo de 50 KB: ruta multipaquete del lector.
            var items = await Drain(client.EnumerateAsync<byte[]>(3));

            items.Should().HaveCount(3);
            for (int i = 0; i < 3; i++)
                items[i].Should().HaveCount(100_000).And.OnlyContain(b => b == i + 1);
        }
    }

    [Fact]
    public async Task ConcurrentStreamsDoNotShareBatchBuffers()
    {
        var (server, client) = Open();
        using (server) using (client)
        {
            // Rondas seguidas de streams concurrentes: los buffers de lote se reutilizan entre ellos.
            for (int round = 0; round < 3; round++)
            {
                var runs = Enumerable.Range(0, 16).Select(seed => Task.Run(async () =>
                    (seed, items: await Drain(client.EnumerateAsync<int, int>(4, seed)))));

                foreach (var (seed, items) in await Task.WhenAll(runs))
                    items.Should().Equal(Enumerable.Range(seed * 1000, 500));
            }
        }
    }

    [Fact]
    public async Task WatchdogCancelsStalledStream()
    {
        var (server, client) = Open(timeout: 500);
        using (server) using (client)
        {
            var sw = Stopwatch.StartNew();
            var drain = async () => await Drain(client.EnumerateAsync<int>(5));

            await drain.Should().ThrowAsync<TimeoutException>();
            sw.ElapsedMilliseconds.Should().BeLessThan(2500, "el watchdog corta antes de que el productor reanude (3 s)");
        }
    }
}
