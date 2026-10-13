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

    /// <summary>Enumerador manual: salida temprana libera el sink pooled, la misma secuencia se reenumera y la conexion sigue sana.</summary>
    [Fact]
    public async Task TestEarlyExitReenumerateAndReuse()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lsl-{Guid.NewGuid():N}.sock");
        using var uds = Server.StartUdsServer(path, async (id, ctx, token) =>
            id == 1 ? await ctx.EnumerateAsync(() => Enumerable.Range(0, 1000)) : await ctx.ReturnAsync(ctx.Get<int>() * 2), () => { });

        await using var connection = new UdsConnection(path);
        var token = new CancellationTokenSource(5000).Token;
        var stream = connection.EnumerateAsync<int>(1, token);

        for (int round = 0; round < 20; round++)
        {
            int seen = 0;
            await foreach (var i in stream)
            {
                i.Should().Be(seen);
                if (++seen == 5 + round) break;
            }
            seen.Should().Be(5 + round);

            var all = new List<int>();
            await foreach (var i in stream) all.Add(i);
            all.Should().Equal(Enumerable.Range(0, 1000));
        }

        (await connection.GetAsync<int, int>(0, 21, token)).Should().Be(42);
    }

    /// <summary>
    /// S1-S3: handler que termina asincrono (exito y fallo tras await) sin DispatchAsync ni registro inflight. Mas peticiones
    /// que huecos por conexion: si el contexto no liberase cuerpo/hueco, la conexion se bloquearia.
    /// </summary>
    [Fact]
    public async Task TestAsyncHandlerCompletionReleasesSlots()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lsl-{Guid.NewGuid():N}.sock");
        using var uds = Server.StartUdsServer(path, async (id, ctx, token) =>
        {
            await Task.Yield();
            int n = ctx.Get<int>();
            return n % 3 == 0 ? throw new ArgumentException($"boom{n}") : await ctx.ReturnAsync(n * 2);
        }, () => { });

        await using var connection = new UdsConnection(path);
        var token = new CancellationTokenSource(10_000).Token;

        var calls = Enumerable.Range(1, Server.MaxInFlightPerConnection * 4).Select(async n =>
        {
            try { return (await connection.GetAsync<int, int>(0, n, token)) == n * 2 && n % 3 != 0; }
            catch (InvalidOperationException ex) { return n % 3 == 0 && ex.Message.Contains($"boom{n}"); }
        });

        (await Task.WhenAll(calls)).Should().AllBeEquivalentTo(true);
    }

    /// <summary>
    /// Tabla de llamadas en curso: un stream abierto ocupa su slot mientras mas de 4096 unarias dan la vuelta al
    /// contador; la que cae en ese slot salta al id siguiente y el stream sigue recibiendo solo lo suyo.
    /// </summary>
    [Fact]
    public async Task TestPendingSlotWrapSkipsLiveStream()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lsl-{Guid.NewGuid():N}.sock");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var uds = Server.StartUdsServer(path, async (id, ctx, token) =>
            id == 1 ? await ctx.EnumerateAsync(() => Gated()) : await ctx.ReturnAsync(ctx.Get<int>() * 2), () => { });

        await using var connection = new UdsConnection(path);
        var token = new CancellationTokenSource(30_000).Token;

        await using var stream = connection.EnumerateAsync<int>(1, token).GetAsyncEnumerator(token);
        (await stream.MoveNextAsync()).Should().BeTrue();
        stream.Current.Should().Be(-1);

        for (int n = 0; n < 5000; n++) (await connection.GetAsync<int, int>(0, n, token)).Should().Be(n * 2);

        release.SetResult();
        var rest = new List<int>();
        while (await stream.MoveNextAsync()) rest.Add(stream.Current);
        rest.Should().Equal(Enumerable.Range(0, 100));

        async IAsyncEnumerable<int> Gated()
        {
            yield return -1;
            await release.Task;
            for (int i = 0; i < 100; i++) yield return i;
        }
    }


    /// <summary>
    /// Escritura agrupada: muchas llamadas concurrentes encolan tramas de tamanos muy distintos en el mismo buffer
    /// (que se realoja al crecer); cada cabecera debe quedar bien y cada llamada recibir su eco.
    /// </summary>
    [Fact]
    public async Task TestBatchedSendKeepsFramesIntact()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lsl-{Guid.NewGuid():N}.sock");
        using var uds = Server.StartUdsServer(path, async (id, ctx, token) => await ctx.ReturnAsync(ctx.Get<string>()), () => { });

        await using var connection = new UdsConnection(path);
        var token = new CancellationTokenSource(30_000).Token;

        var calls = Enumerable.Range(0, 2000).Select(n => Task.Run(async () =>
        {
            var text = new string((char)('a' + n % 26), n % 7 == 0 ? 20_000 + n : n % 300);
            return await connection.GetAsync<string, string>(0, text, token) == text;
        }));

        (await Task.WhenAll(calls)).Should().AllBeEquivalentTo(true);
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

    /// <summary>Raw: el cliente escribe la petición en la trama y decodifica la respuesta sobre el buffer del transporte (sin copias).</summary>
    [Fact]
    public async Task TestRawRoundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lsl-{Guid.NewGuid():N}.sock");
        using var uds = Server.StartUdsServer(path, new Doubler(), () => { });

        await using var connection = new UdsConnection(path);
        var result = await connection.GetRawAsync(0, 21, static (w, v) => MemoryPack.MemoryPackSerializer.Serialize(w, v),
            static b => MemoryPack.MemoryPackSerializer.Deserialize<int>(b), new CancellationTokenSource(3000).Token);
        result.Should().Be(42);
    }
}
