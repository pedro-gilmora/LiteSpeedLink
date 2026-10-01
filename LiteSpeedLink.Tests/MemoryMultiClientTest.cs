using System.Runtime.Versioning;

using FluentAssertions;

using SourceCrafter.LiteSpeedLink.Client;
using SourceCrafter.LiteSpeedLink;

namespace LiteSpeedLink.Tests;

/// <summary>Dos <see cref="MemoryConnection"/> contra el mismo <c>contextId</c>.</summary>
[SupportedOSPlatform("windows")]
public class MemoryMultiClientTest
{

    static Task<byte[]> Handle(long op, MemoryRequestContext ctx, CancellationToken token) => op switch
    {
        0 => Task.FromResult(ctx.Return(ctx.Get<int>() + 1)),
        2 => ctx.Yield(Items()),
        _ => Task.FromResult(ctx.NotFound())
    };

    static async IAsyncEnumerable<int> Items()
    {
        for (int i = 1; i <= 10; i++) { yield return i; await Task.Yield(); }
    }

    [Fact]
    public async Task TwoClientsRequests()
    {
        string name = $"Test-{Guid.CreateVersion7()}";
        using var server = Server.StartMemoryServerAsync(name, Handle, () => { });
        using var a = new MemoryConnection(name, 2000);
        using var b = new MemoryConnection(name, 2000);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
            (await (i % 2 == 0 ? a : b).GetAsync<int, int>(0, i)).Should().Be(i + 1))));
    }

    [Fact]
    public async Task TwoClientsStreams()
    {
        string name = $"Test-{Guid.CreateVersion7()}";
        using var server = Server.StartMemoryServerAsync(name, Handle, () => { });
        using var a = new MemoryConnection(name, 2000);
        using var b = new MemoryConnection(name, 2000);

        var runs = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            List<int> items = [];
            await foreach (var x in (i % 2 == 0 ? a : b).EnumerateAsync<int>(2)) items.Add(x);
            return items;
        }));

        foreach (var items in await Task.WhenAll(runs))
            items.Should().Equal(Enumerable.Range(1, 10));
    }
}
