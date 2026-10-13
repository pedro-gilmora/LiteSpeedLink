using System.Net;
using System.Runtime.Versioning;
using FluentAssertions;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;
using Xunit;

namespace LiteSpeedLink.Tests;

public class StreamBatchPolicyTest
{
    private readonly struct Batched16 : IStreamPolicy
    {
        public static int Items => 16;
        public static long MaxDelayTicks => long.MaxValue;
    }

    private readonly struct Batched1000Timed1 : IStreamPolicy
    {
        public static int Items => 1000;
        public static long MaxDelayTicks => System.Diagnostics.Stopwatch.Frequency / 1000;
    }

    /// <summary>Productor asincrono lento: el lote sale en cuanto el productor espera (no espera a llenar 1000).</summary>
    [Fact]
    public async Task SlowProducerIsNotHeldByBatch()
    {
        using var cts = new CancellationTokenSource(5000);
        var server = Server.StartTcpServer(0, (op, ctx, _) => ctx.EnumerateAsync<int, Batched1000Timed1>(Slow()), () => { });
        try
        {
            await using var client = new TcpConnection(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)server.LocalEndpoint).Port));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await using var e = client.EnumerateAsync<int, int>(1, 0, cts.Token).GetAsyncEnumerator(cts.Token);
            (await e.MoveNextAsync()).Should().BeTrue();
            e.Current.Should().Be(0);
            sw.ElapsedMilliseconds.Should().BeLessThan(1500);
        }
        finally { server.Stop(); }

        static async IAsyncEnumerable<int> Slow()
        {
            for (int i = 0; i < 5; i++) { yield return i; await Task.Delay(500); }
        }
    }

    /// <summary>Estudio #1: la politica emitida por operacion ([Stream(Batch=…)]) manda sobre la del servidor (sin lotes).</summary>
    [Fact]
    public async Task PerOperationBatchOverridesServer()
    {
        using var cts = new CancellationTokenSource(5000);
        var server = Server.StartTcpServer(0, (op, ctx, _) => ctx.EnumerateAsync<int, Batched16>(Enumerable.Range(0, ctx.Get<int>())), () => { });
        try
        {
            await using var client = new TcpConnection(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)server.LocalEndpoint).Port));
            var items = new List<int>();
            await foreach (var i in client.EnumerateAsync<int, int>(1, 1000, cts.Token)) items.Add(i);
            items.Should().Equal(Enumerable.Range(0, 1000));
        }
        finally { server.Stop(); }
    }

    /// <summary>QUIC con cada politica (item a item, por defecto, lote 16): mismo orden y mismos items.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    public async Task QuicStreamsMatchBatched(int policy)
    {
        var cert = Constants.GetDevCert();
        await using var server = await Server.StartQuicServerAsync(0, (op, ctx, _) =>
        {
            var items = Enumerable.Range(0, ctx.Get<int>()).Select(i => $"item-{i}");
            return policy switch
            {
                0 => ctx.EnumerateAsync<string, Unbatched>(items),
                1 => ctx.EnumerateAsync(items),
                _ => ctx.EnumerateAsync<string, Batched16>(items),
            };
        }, () => { }, cert);
        await using var client = new DnsEndPoint("localhost", server.LocalEndPoint.Port).AsQuicConnection(cert);
        foreach (var count in new[] { 0, 1, 7, 1000 })
        {
            var items = new List<string?>();
            await foreach (var s in client.EnumerateAsync<int, string>(1, count)) items.Add(s);
            items.Should().Equal(Enumerable.Range(0, count).Select(i => $"item-{i}"));
        }
    }

    /// <summary>Punto 7: el lote ya no es configurable en runtime; la politica solo la fija el contrato ([Stream]) en compilacion.</summary>
    [Fact]
    public void NoRuntimeBatchKnob()
    {
        typeof(Server).GetMethods()
            .Where(m => m.Name is nameof(Server.StartTcpServer) or nameof(Server.StartUdsServer) or nameof(Server.StartQuicServerAsync))
            .SelectMany(m => m.GetParameters())
            .Should().NotContain(p => p.Name!.StartsWith("streamBatch"));
    }
}
