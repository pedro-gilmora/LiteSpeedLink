using System.Net;
using FluentAssertions;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;
using Xunit;

namespace LiteSpeedLink.Tests;

/// <summary>Estudio #1: cada combinacion secuencia x politica-tipo que emite el host generado (ver StreamService en el sample).</summary>
public class StreamPolicyTypeTest
{
    private readonly struct Batched16 : IStreamPolicy
    {
        public static int Items => 16;
        public static long MaxDelayTicks => long.MaxValue;
    }

    private readonly struct Timed16 : IStreamPolicy
    {
        public static int Items => 16;
        public static long MaxDelayTicks => System.Diagnostics.Stopwatch.Frequency * 1 / 1000;
    }

    private static async IAsyncEnumerable<int> Range(int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (i % 100 == 99) await Task.Yield();
            yield return i;
        }
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 3)]
    [InlineData(1, 0)] [InlineData(1, 3)]
    [InlineData(2, 0)] [InlineData(2, 3)]
    [InlineData(3, 0)] [InlineData(3, 3)]
    [InlineData(4, 0)] [InlineData(4, 3)]
    [InlineData(5, 0)] [InlineData(5, 3)]
    [InlineData(6, 0)] [InlineData(6, 3)]
    [InlineData(7, 0)] [InlineData(7, 3)]
    public async Task EveryShapeDeliversInOrder(long op, int serverBatch)
    {
        using var cts = new CancellationTokenSource(5000);
        var server = Server.StartTcpServer(0, (op, ctx, _) =>
        {
            int n = ctx.Get<int>();
            return op switch
            {
                0 => ctx.EnumerateAsync(Enumerable.Range(0, n)),
                1 => ctx.EnumerateAsync<int, Unbatched>(Enumerable.Range(0, n)),
                2 => ctx.EnumerateAsync<int, Batched16>(Enumerable.Range(0, n)),
                3 => ctx.EnumerateAsync<int, Timed16>(Enumerable.Range(0, n)),
                4 => ctx.EnumerateAsync(Range(n)),
                5 => ctx.EnumerateAsync<int, Unbatched>(Range(n)),
                6 => ctx.EnumerateAsync<int, Batched16>(Range(n)),
                _ => ctx.EnumerateAsync<int, Timed16>(Range(n)),
            };
        }, () => { }, streamBatch: serverBatch);
        try
        {
            await using var client = new TcpConnection(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)server.LocalEndpoint).Port));
            foreach (var count in new[] { 0, 1, 16, 17, 1000 })
            {
                var items = new List<int>();
                await foreach (var i in client.EnumerateAsync<int, int>(op, count, cts.Token)) items.Add(i);
                items.Should().Equal(Enumerable.Range(0, count));
            }
        }
        finally { server.Stop(); }
    }
}
