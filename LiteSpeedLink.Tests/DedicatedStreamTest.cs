using System.Net;
using System.Runtime.Versioning;
using FluentAssertions;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;

namespace LiteSpeedLink.Tests;

/// <summary>#14: [DedicatedStream] saca la unaria del pool QUIC; el resto de transportes lo ignora.</summary>
public class DedicatedStreamTest
{
    static string Source(string transport) => $$"""
        using SourceCrafter.DependencyInjection.Attributes;
        using SourceCrafter.LiteSpeedLink;

        namespace Probe;

        public interface IContract : IServiceUnit
        {
            [DedicatedStream] byte[] Big(int size);
            [DedicatedStream] System.Collections.Generic.IAsyncEnumerable<int> Feed(int count);
            int Small(int value);
        }

        [ServiceClient(ServiceConnectionType.{{transport}})]
        [ServiceUnit<IContract>]
        [ServiceProvider]
        public sealed partial class Client;
        """;

    [Theory]
    [InlineData("Quic", true)]
    [InlineData("Tcp", false)]
    public void RoutesOnlyQuicToDedicated(string transport, bool dedicated)
    {
        var r = GeneratorHarness.Run(Source(transport));

        r.Errors.Should().BeEmpty();
        var client = r.Sources.Single(s => s.Key.Contains("Contract.client")).Value;
        // Big: sync + async por la vista dedicada; Feed: stream dedicado; Small sigue por el pool.
        client.Split("__connection.Dedicated.").Length.Should().Be(dedicated ? 4 : 1);
        client.Should().Contain("__connection.GetRaw");
    }

    /// <summary>Punto 2: streams QUIC por el pool multiplexado (por defecto) y por la vista dedicada; concurrentes e intercalados.</summary>
    [Fact]
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    public async Task StreamsRunOnPoolAndDedicated()
    {
        var cert = Constants.GetDevCert();
        await using var server = await Server.StartQuicServerAsync(0, (op, ctx, _) => ctx.EnumerateAsync(() => Enumerable.Range(0, (int)op)), () => { }, cert);
        await using var connection = new DnsEndPoint("localhost", server.LocalEndPoint.Port).AsQuicConnection(cert, unaryStreams: 2);

        static async Task<List<int>> Drain(IAsyncEnumerable<int> e) { var l = new List<int>(); await foreach (var i in e) l.Add(i); return l; }

        var runs = Enumerable.Range(1, 40).Select(n => Drain((n % 2 == 0 ? connection : connection.Dedicated).EnumerateAsync<int>(n * 10))).ToArray();
        var results = await Task.WhenAll(runs);

        for (int n = 1; n <= 40; n++) results[n - 1].Should().Equal(Enumerable.Range(0, n * 10));
    }

    [Fact]
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    public async Task DedicatedSharesConnection()
    {
        var cert = Constants.GetDevCert();
        byte[] big = new byte[256 * 1024];
        await using var server = await Server.StartQuicServerAsync(0, (op, ctx, _) => op == 1 ? ctx.ReturnAsync(7) : ctx.ReturnAsync(big), () => { }, cert);
        await using var connection = new DnsEndPoint("localhost", server.LocalEndPoint.Port).AsQuicConnection(cert);

        var dedicated = connection.Dedicated;
        dedicated.Should().NotBeSameAs(connection).And.BeSameAs(connection.Dedicated);
        dedicated.Dedicated.Should().BeSameAs(dedicated, "sin pool ya es dedicada");

        var bigs = Enumerable.Range(0, 4).Select(_ => dedicated.GetAsync<int, byte[]>(2, 0).AsTask()).ToArray();
        (await connection.GetAsync<int, int>(1, 0)).Should().Be(7);
        (await Task.WhenAll(bigs)).Should().OnlyContain(b => b!.Length == big.Length);
    }
}
