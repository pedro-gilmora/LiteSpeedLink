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

    /// <summary>Productor asincrono lento: el lote sale en cuanto el productor espera (no espera a llenar 1000).</summary>
    [Fact]
    public async Task SlowProducerIsNotHeldByBatch()
    {
        using var cts = new CancellationTokenSource(5000);
        var server = Server.StartTcpServer(0, (op, ctx, _) => ctx.EnumerateAsync(() => Slow()), () => { }, streamBatch: 1000, streamBatchMaxDelay: TimeSpan.FromMilliseconds(1));
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

    /// <summary>QUIC con lotes: mismo orden y mismos items.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    public async Task QuicStreamsMatchBatched(int streamBatch)
    {
        int port = 5040 + streamBatch;
        var cert = Constants.GetDevCert();
        await using var server = await Server.StartQuicServerAsync(port, (op, ctx, _) => ctx.EnumerateAsync(() => Enumerable.Range(0, ctx.Get<int>()).Select(i => $"item-{i}")), () => { }, cert, default, streamBatch);
        await using var client = new DnsEndPoint("localhost", port).AsQuicConnection(cert);
        foreach (var count in new[] { 0, 1, 7, 1000 })
        {
            var items = new List<string?>();
            await foreach (var s in client.EnumerateAsync<int, string>(1, count)) items.Add(s);
            items.Should().Equal(Enumerable.Range(0, count).Select(i => $"item-{i}"));
        }
    }
}
