using FluentAssertions;

namespace LiteSpeedLink.Tests;

/// <summary>La conexion del cliente la libera el Dispose/DisposeAsync que emite el generador de DI.</summary>
public class ClientDisposalTest
{
    const string Source = """
        using SourceCrafter.DependencyInjection.Attributes;
        using SourceCrafter.LiteSpeedLink;

        namespace Probe;

        public interface IContract : IServiceUnit
        {
            int Echo(int value);
        }

        [ServiceClient(ServiceConnectionType.TRANSPORT)]
        [ServiceUnit<IContract>]
        [ServiceProvider]
        public sealed partial class Client;

        public static class Use
        {
            public static async System.Threading.Tasks.Task Run()
            {
                USE
            }
        }
        """;

    [Theory]
    [InlineData("Memory", "using var c = new Client(\"probe\");", "__connection.Dispose();")]
    [InlineData("Udp", "using var c = new Client(\"localhost\", 1);", "__connection.Dispose();")]
    [InlineData("Tcp", "await using var c = new Client(\"localhost\", 1);", "__connection.DisposeAsync()")]
    [InlineData("Local", "await using var c = new Client(\"probe\");", "__connection.DisposeAsync()")]
    public void ProviderDisposesConnection(string transport, string use, string disposal)
    {
        var r = GeneratorHarness.Run(
            Source.Replace("TRANSPORT", transport).Replace("USE", use),
            new Dictionary<string, string> { ["build_property.TargetFramework"] = "net10.0-windows" });

        r.Errors.Should().BeEmpty();
        r.Sources.Single(s => s.Key.EndsWith("Client.g.cs") && !s.Key.Contains(".client")).Value.Should().Contain(disposal);
    }
}
