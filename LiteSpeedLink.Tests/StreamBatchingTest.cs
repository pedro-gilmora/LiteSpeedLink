using System.Net;
using FluentAssertions;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;
using Xunit;

namespace LiteSpeedLink.Tests;

public class StreamBatchingTest
{
    private readonly struct Batched7 : IStreamPolicy
    {
        public static int Items => 7;
        public static long MaxDelayTicks => long.MaxValue;
    }

    /// <summary>Politica del servidor (item a item, por defecto, lote 7) x cliente que agrupa; mismo orden y mismos items, unarias intactas.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task StreamsMatchInEveryMode(int policy, bool coalesce)
    {
        using var cts = new CancellationTokenSource(5000);
        var server = Server.StartTcpServer(0, (op, ctx, _) =>
        {
            if (op == 0) return ctx.ReturnAsync(ctx.Get<int>() * 2);
            var items = Enumerable.Range(0, ctx.Get<int>()).Select(i => $"item-{i}");
            return policy switch
            {
                0 => ctx.EnumerateAsync<string, Unbatched>(items),
                1 => ctx.EnumerateAsync(items),
                _ => ctx.EnumerateAsync<string, Batched7>(items),
            };
        }, () => { });

        try
        {
            await using var client = new TcpConnection(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)server.LocalEndpoint).Port), coalesceStreams: coalesce);

            foreach (var count in new[] { 0, 1, 7, 1000 })
            {
                var items = new List<string?>();
                await foreach (var s in client.EnumerateAsync<int, string>(1, count, cts.Token)) items.Add(s);
                items.Should().Equal(Enumerable.Range(0, count).Select(i => $"item-{i}"));
            }

            var calls = Enumerable.Range(0, 50).Select(i => client.GetAsync<int, int>(0, i, cts.Token).AsTask());
            (await Task.WhenAll(calls)).Should().Equal(Enumerable.Range(0, 50).Select(i => i * 2));
        }
        finally
        {
            server.Stop();
        }
    }
}
