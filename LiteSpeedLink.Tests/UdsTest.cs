using FluentAssertions;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;
using Xunit;

namespace LiteSpeedLink.Tests;

public class UdsTest
{
    /// <summary>5.6: Unix Domain Socket (Windows 10+/Linux/macOS) con get y stream sobre una conexion.</summary>
    [Fact]
    public async Task TestUdsRoundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lsl-{Guid.NewGuid():N}.sock");
        using var uds = Server.StartUdsServer(path, async (id, ctx, token) =>
            id == 1 ? await ctx.EnumerateAsync(() => Enumerable.Range(0, 3)) : await ctx.ReturnAsync(ctx.Get<int>() * 2), () => { });

        await using var connection = new UdsConnection(path);
        (await connection.GetAsync<int, int>(0, 21, new CancellationTokenSource(3000).Token)).Should().Be(42);

        var items = new List<int>();
        await foreach (var i in connection.EnumerateAsync<int>(1, new CancellationTokenSource(3000).Token)) items.Add(i);
        items.Should().Equal(0, 1, 2);
    }

    private readonly struct Doubler : IRequestHandler
    {
        public ValueTask<ResponseStatus> HandleAsync(long id, RequestContext ctx, CancellationToken token) => ctx.ReturnAsync(ctx.Get<int>() * 2);
    }

    /// <summary>Estudio #3: host como struct IRequestHandler (lo que emite el generador), sin delegado.</summary>
    [Fact]
    public async Task TestStructHandlerRoundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lsl-{Guid.NewGuid():N}.sock");
        using var uds = Server.StartUdsServer(path, new Doubler(), () => { });

        await using var connection = new UdsConnection(path);
        (await connection.GetAsync<int, int>(0, 21, new CancellationTokenSource(3000).Token)).Should().Be(42);
    }

    /// <summary>Raw: el cliente construye los bytes de la petición y recibe los bytes de la respuesta.</summary>
    [Fact]
    public async Task TestRawRoundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lsl-{Guid.NewGuid():N}.sock");
        using var uds = Server.StartUdsServer(path, new Doubler(), () => { });

        await using var connection = new UdsConnection(path);
        var body = await connection.GetRawAsync(0, MemoryPack.MemoryPackSerializer.Serialize(21), new CancellationTokenSource(3000).Token);
        MemoryPack.MemoryPackSerializer.Deserialize<int>(body.Span).Should().Be(42);
    }
}
