using FluentAssertions;
using SourceCrafter.LiteSpeedLink;
using Xunit;

namespace LiteSpeedLink.Tests;

public class CacheManagerTest
{
    [Fact]
    public void ServerMatchesByContentAndCopiesBuffers()
    {
        var cache = new ServerCacheManager(60_000, 8);
        byte[] request = [1, 2, 3], response = [9, 8];

        cache.Set(request, response);

        // Buffers reutilizados (pool): mutarlos no puede alterar lo guardado.
        request[0] = 7;
        response[0] = 0;

        cache.TryGet(new byte[] { 1, 2, 3 }, out var hit).Should().BeTrue();
        hit.ToArray().Should().Equal(9, 8);

        // Mismo contenido en otro buffer y con offset: igual clave.
        cache.TryGet(new byte[] { 0, 1, 2, 3 }.AsMemory(1), out _).Should().BeTrue();
        cache.TryGet(new byte[] { 1, 2 }, out _).Should().BeFalse();
    }

    [Fact]
    public void ComparerHashesByContent()
    {
        var comparer = ByteContentComparer.Instance;
        ReadOnlyMemory<byte> a = new byte[] { 1, 2, 3 }, b = new byte[] { 0, 1, 2, 3 }.AsMemory(1);

        a.Equals(b).Should().BeFalse("ReadOnlyMemory compara referencia/offset/longitud");
        comparer.Equals(a, b).Should().BeTrue();
        comparer.GetHashCode(a).Should().Be(comparer.GetHashCode(b));
        comparer.GetHashCode(a.Span).Should().Be(comparer.GetHashCode(b));
    }

    [Fact]
    public void ExpiresAndRespectsCapacity()
    {
        var server = new ServerCacheManager(50, 1);
        server.Set(new byte[] { 1 }, new byte[] { 1 });
        server.Set(new byte[] { 2 }, new byte[] { 2 });

        server.TryGet(new byte[] { 2 }, out _).Should().BeFalse("llena y sin expiradas: no se guarda");

        Thread.Sleep(100);

        server.TryGet(new byte[] { 1 }, out _).Should().BeFalse();
        server.Set(new byte[] { 2 }, new byte[] { 2 });
        server.TryGet(new byte[] { 2 }, out _).Should().BeTrue("la expirada se purgo");

        var client = new CacheManager<int, string>(50, 1);
        client.Set(1, "a");
        client.Set(2, "b");

        client.TryGet(1, out var a).Should().BeTrue();
        a.Should().Be("a");
        client.TryGet(2, out _).Should().BeFalse();

        Thread.Sleep(100);

        client.TryGet(1, out _).Should().BeFalse();
        client.Set(2, "b");
        client.TryGet(2, out var b).Should().BeTrue();
        b.Should().Be("b");
    }

    [Fact]
    public void ClientKeyKeepsValuesSoEqualHashesDoNotCollide()
    {
        var cache = new CacheManager<CollidingKey, int>(60_000, 8);

        cache.Set(new(1), 10);
        cache.Set(new(2), 20);

        cache.TryGet(new(1), out var one).Should().BeTrue();
        cache.TryGet(new(2), out var two).Should().BeTrue();
        (one, two).Should().Be((10, 20));
    }

    [Fact]
    public async Task SingleFlightRunsHandlerOnceAndNeverCachesFailures()
    {
        var server = new ServerCacheManager(60_000, 8);
        byte[] request = [1, 2];

        server.TryLead(request, out var lead).Should().BeTrue();
        server.TryLead(new byte[] { 1, 2 }, out var follower).Should().BeFalse("misma peticion en curso");
        ReferenceEquals(follower, lead).Should().BeTrue();

        server.Fail(request, lead, new InvalidOperationException());
        await follower.Awaiting(f => f.Task).Should().ThrowAsync<InvalidOperationException>();
        server.TryGet(request, out _).Should().BeFalse("los fallos no se cachean");

        server.TryLead(request, out lead).Should().BeTrue("tras el fallo se reintenta");
        server.TryLead(request, out follower).Should().BeFalse();
        server.Complete(request, lead, new byte[] { 7 });
        (await follower.Task).ToArray().Should().Equal(7);
        server.TryGet(request, out var hit).Should().BeTrue();
        hit.ToArray().Should().Equal(7);

        // Lider anterior ya publico: TryLead no vuelve a liderar.
        server.TryLead(request, out var late).Should().BeFalse();
        (await late.Task).ToArray().Should().Equal(7);

        var client = new CacheManager<(int, string), string>(60_000, 8);
        client.TryLead((1, "a"), out var cLead).Should().BeTrue();
        client.TryLead((1, "a"), out var cFollower).Should().BeFalse();
        client.TryLead((1, "b"), out var other).Should().BeTrue("otra clave, otro vuelo");
        client.Complete((1, "a"), cLead, "x");
        (await cFollower.Task).Should().Be("x");
        client.Fail((1, "b"), other, new OperationCanceledException());
        client.TryLead((1, "b"), out _).Should().BeTrue();
    }

    private readonly record struct CollidingKey(int Value)
    {
        public override int GetHashCode() => 0;
    }
}
