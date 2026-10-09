using FluentAssertions;

namespace LiteSpeedLink.Tests;

/// <summary>Paso 11: <c>ServiceConnectionType.Local</c> lo resuelve el generador con las build properties.</summary>
public class LocalConnectionTest
{
    const string Source = """
        using SourceCrafter.DependencyInjection.Attributes;
        using SourceCrafter.LiteSpeedLink;

        namespace Probe;

        public interface IContract : IServiceUnit
        {
            int Echo(int value);
        }

        public sealed class Contract : IContract
        {
            public int Echo(int value) => value;
        }

        [ServiceHost(ServiceConnectionType.Local)]
        [ServiceProvider]
        [Singleton<IContract, Contract>]
        public sealed partial class Host;

        [ServiceClient(ServiceConnectionType.Local)]
        [ServiceUnit<IContract>]
        [ServiceProvider]
        public sealed partial class Client;

        public static class Use
        {
            public static async System.Threading.Tasks.Task Run()
            {
                await using var host = Host.Start("probe");
                await using var client = new Client("probe");
            }
        }
        """;

    [Theory]
    [InlineData("net10.0-windows", "", true)]
    [InlineData("net10.0", "win-x64", true)]
    [InlineData("net10.0-windows", "linux-x64", false)]
    [InlineData("net10.0", "linux-x64", false)]
    [InlineData("net10.0", "", null)]
    public void ResolvesByTarget(string tfm, string rid, bool? windows)
    {
        // Sin RID ni TFM con plataforma decide el SO donde compila.
        var isWindows = windows ?? OperatingSystem.IsWindows();
        var (hostApi, clientApi) = isWindows
            ? ("StartMemoryServer", "AsMemoryConnection")
            : ("StartUdsServer", "UdsConnection(");
        const string attr = "[global::System.Runtime.Versioning.SupportedOSPlatform(\"windows\")]";

        var r = GeneratorHarness.Run(Source, new Dictionary<string, string>
        {
            ["build_property.TargetFramework"] = tfm,
            ["build_property.RuntimeIdentifier"] = rid,
        });

        r.Errors.Should().BeEmpty();
        var host = r.Sources.Single(s => s.Key.Contains("Host.host")).Value;
        var client = r.Sources.Single(s => s.Key.Contains("Client.client")).Value;
        host.Should().Contain(hostApi).And.Contain("global::System.IAsyncDisposable Start(");
        client.Should().Contain(clientApi).And.Contain(": global::System.IAsyncDisposable");
        host.Contains(attr).Should().Be(isWindows);
        client.Contains(attr).Should().Be(isWindows);
    }

    [Fact]
    public void QuicAttributesAreGenerated()
    {
        var r = GeneratorHarness.Run(Source.Replace("ServiceConnectionType.Local", "ServiceConnectionType.Quic"));

        // El uso de ejemplo de Source es de Local (Start(string)); solo importan los errores del codigo generado.
        r.Errors.Where(e => !e.Location.SourceTree!.FilePath.EndsWith("Probe.cs")).Should().BeEmpty();
        foreach (var key in new[] { "Host.host", "Client.client" })
            r.Sources.Single(s => s.Key.Contains(key)).Value
                .Should().Contain("[global::System.Runtime.Versioning.RequiresPreviewFeatures]")
                .And.Contain("[global::System.Runtime.Versioning.SupportedOSPlatform(\"linux\")]");
    }
}
