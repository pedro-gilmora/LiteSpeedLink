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
        // Big: sync + async por la vista dedicada; Small sigue por el pool.
        client.Split("__connection.Dedicated.").Length.Should().Be(dedicated ? 3 : 1);
        client.Should().Contain("__connection.GetRaw");
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
