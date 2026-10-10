using FluentAssertions;

namespace LiteSpeedLink.Tests;

/// <summary>5.8: endpoint omitido en <c>Start</c>/constructor -> <c>[JsonSetting&lt;RemoteOptions|LocalOptions&gt;]</c>; el explicito gana.</summary>
public class EndpointSettingsTest
{
    const string Source = """
        using SourceCrafter.DependencyInjection.Attributes;
        using SourceCrafter.DependencyInjection.MsConfiguration.Metadata;
        using SourceCrafter.LiteSpeedLink;

        [assembly: JsonConfiguration]

        namespace Probe;

        public interface IContract : IServiceUnit
        {
            int Echo(int value);
        }

        public sealed class Contract : IContract
        {
            public int Echo(int value) => value;
        }

        [ServiceHost(ServiceConnectionType.TRANSPORT)]
        [ServiceProvider]
        [Singleton<IContract, Contract>]
        SETTING
        public sealed partial class Host;

        [ServiceClient(ServiceConnectionType.TRANSPORT)]
        [ServiceUnit<IContract>]
        [ServiceProvider]
        SETTING
        public sealed partial class Client;

        public static class Use
        {
            public static void Run()
            {
                USE
            }
        }
        """;

    static GeneratorHarness.Result Run(string transport, string setting, string use) => GeneratorHarness.Run(
        Source.Replace("TRANSPORT", transport).Replace("SETTING", setting).Replace("USE", use),
        new Dictionary<string, string> { ["build_property.TargetFramework"] = "net10.0-windows" },
        msConfig: true);

    [Fact]
    public void RemoteFallsBackToSettings()
    {
        var r = Run("Udp", """[JsonSetting<RemoteOptions>("Endpoint")]""", """
            using var host = Host.Start();
            using var explicitHost = Host.Start(1234);
            var client = new Client();
            var partial = new Client(port: 1234);
            var full = new Client("localhost", 1234);
            """);

        r.Errors.Should().BeEmpty();
        r.Sources.Single(s => s.Key.Contains("Host.host")).Value.Should().Contain("provider.Settings.Port");
        r.Sources.Single(s => s.Key.Contains("Client.client")).Value
            .Should().Contain("hostname ?? Settings.Host").And.Contain("port ?? Settings.Port");
    }

    [Fact]
    public void LocalFallsBackToSettingsWithCustomName()
    {
        var r = Run("Local", """[JsonSetting<LocalOptions>("Endpoint", key: "lsl")]""", """
            var host = Host.Start();
            var explicitHost = Host.Start("probe");
            var client = new Client();
            var named = new Client("probe");
            """);

        r.Errors.Should().BeEmpty();
        r.Sources.Single(s => s.Key.Contains("Client.client")).Value
            .Should().Contain("name ??= LslSettings.Name").And.Contain("LslSettings.Timeout");
    }

    [Fact]
    public void WithoutSettingEndpointIsRequired()
    {
        // Sin [JsonConfiguration]: con ella, un contenedor sin servicios DI no recibe EnvironmentName (CS0103 en el msConfig, fallo de MsConfiguration).
        var r = GeneratorHarness.Run(Source.Replace("[assembly: JsonConfiguration]", "").Replace("TRANSPORT", "Tcp").Replace("SETTING", "").Replace("USE", ""), msConfig: true);

        r.Errors.Should().BeEmpty();
        r.Sources.Single(s => s.Key.Contains("Host.host")).Value.Should().NotContain("Settings");
        r.Sources.Single(s => s.Key.Contains("Client.client")).Value.Should().Contain("(string hostname, int port)");
    }
}
