using FluentAssertions;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;
using Xunit;

namespace LiteSpeedLink.Tests;

public class PooledEnumeratorTest
{
    /// <summary>Enumeradores pooled: dos a la vez del mismo stream no comparten estado y un doble Dispose no duplica la instancia en el pool.</summary>
    [Fact]
    public async Task TestPooledEnumeratorsAreIndependent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lsl-{Guid.NewGuid():N}.sock");
        using var uds = Server.StartUdsServer(path, async (id, ctx, token) =>
            await ctx.EnumerateAsync(() => Enumerable.Range(0, 100)), () => { });

        await using var connection = new UdsConnection(path);
        var token = new CancellationTokenSource(5000).Token;
        var stream = connection.EnumerateAsync<int>(1, token);

        for (int round = 0; round < 10; round++)
        {
            var a = stream.GetAsyncEnumerator(token);
            (await a.MoveNextAsync()).Should().BeTrue();
            await a.DisposeAsync();
            await a.DisposeAsync();

            var b = stream.GetAsyncEnumerator(token);
            var c = stream.GetAsyncEnumerator(token);
            b.Should().NotBeSameAs(c);

            for (int i = 0; i < 100; i++)
            {
                (await b.MoveNextAsync()).Should().BeTrue();
                (await c.MoveNextAsync()).Should().BeTrue();
                b.Current.Should().Be(i);
                c.Current.Should().Be(i);
            }

            (await b.MoveNextAsync()).Should().BeFalse();
            (await c.MoveNextAsync()).Should().BeFalse();
            await b.DisposeAsync();
            await c.DisposeAsync();
        }
    }
}
