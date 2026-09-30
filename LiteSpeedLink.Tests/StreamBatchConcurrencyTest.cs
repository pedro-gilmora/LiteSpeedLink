using System.Net;
using FluentAssertions;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;
using Xunit;

namespace LiteSpeedLink.Tests;

public class StreamBatchConcurrencyTest
{
    /// <summary>Streams y unarias entrelazados en una misma conexion: cada llamada recibe solo lo suyo y en orden.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(16, false)]
    [InlineData(16, true)]
    public async Task ConcurrentCallsOnOneConnectionDoNotMix(int streamBatch, bool coalesce)
    {
        using var cts = new CancellationTokenSource(10000);
        var server = Server.StartTcpServer(0, (op, ctx, _) => op == 0
            ? ctx.ReturnAsync(ctx.Get<int>() * 2)
            : ctx.EnumerateAsync(() => Tagged(ctx.Get<int>())), () => { }, streamBatch: streamBatch);

        try
        {
            await using var client = new TcpConnection(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)server.LocalEndpoint).Port), coalesceStreams: coalesce);

            var streams = Enumerable.Range(1, 32).Select(async tag =>
            {
                var items = new List<int>();
                await foreach (var v in client.EnumerateAsync<int, int>(1, tag, cts.Token)) items.Add(v);
                items.Should().Equal(Enumerable.Range(0, 500).Select(i => tag * 1_000_000 + i));
            });
            var unaries = Enumerable.Range(0, 200).Select(async i => (await client.GetAsync<int, int>(0, i, cts.Token)).Should().Be(i * 2));

            await Task.WhenAll(streams.Concat(unaries));
        }
        finally
        {
            server.Stop();
        }

        static async IAsyncEnumerable<int> Tagged(int tag)
        {
            for (int i = 0; i < 500; i++)
            {
                if (i % 50 == 0) await Task.Yield();
                yield return tag * 1_000_000 + i;
            }
        }
    }
}
