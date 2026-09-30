using System.Net;
using FluentAssertions;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;
using Xunit;

namespace LiteSpeedLink.Tests;

public class StreamBatchingTest
{
    /// <summary>Opt-in: servidor por lotes y/o cliente que agrupa; mismo orden y mismos items, unarias intactas.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(7, false)]
    [InlineData(7, true)]
    public async Task StreamsMatchInEveryMode(int streamBatch, bool coalesce)
    {
        using var cts = new CancellationTokenSource(5000);
        var server = Server.StartTcpServer(0, (op, ctx, _) => op == 0
            ? ctx.ReturnAsync(ctx.Get<int>() * 2)
            : ctx.EnumerateAsync(() => Enumerable.Range(0, ctx.Get<int>()).Select(i => $"item-{i}")), () => { }, streamBatch: streamBatch);

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
