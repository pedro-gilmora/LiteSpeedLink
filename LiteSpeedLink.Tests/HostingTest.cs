using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;

namespace LiteSpeedLink.Tests;

/// <summary>Cualquier IHost: <c>services.AddLiteSpeedLink(new THost.Endpoint { ... })</c> (paquete Hosting).</summary>
public class HostingTest
{
    const string Cert = "global::System.Security.Cryptography.X509Certificates.X509Certificate2";

    const string Source = """
        using SourceCrafter.DependencyInjection.Attributes;
        using SourceCrafter.DependencyInjection.MsConfiguration.Metadata;
        using SourceCrafter.LiteSpeedLink;

        CONFIG

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

        public static class Use
        {
            public static void Run(Microsoft.Extensions.DependencyInjection.IServiceCollection services, System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
            {
                USE
            }
        }
        """;

    static GeneratorHarness.Result Generate(string transport, string use, string setting = "") => GeneratorHarness.Run(
        Source.Replace("TRANSPORT", transport).Replace("USE", use).Replace("SETTING", setting)
            .Replace("CONFIG", setting.Length > 0 ? "[assembly: JsonConfiguration]" : ""),
        new Dictionary<string, string> { ["build_property.TargetFramework"] = "net10.0-windows" },
        msConfig: setting.Length > 0);

    static string HostOf(GeneratorHarness.Result r) => r.Sources.Single(s => s.Key.Contains("Host.host")).Value;

    /// <summary>Lo que el transporte y el TLS exigen es required: sin puerto o sin certificado (QUIC) no compila (CS9035).</summary>
    [Theory]
    [InlineData("Tcp", "services.AddLiteSpeedLink(new Host.Endpoint { Port = 5000 });", null)]
    [InlineData("Tcp", "services.AddLiteSpeedLink(new Host.Endpoint());", "CS9035")]
    [InlineData("Quic", "services.AddLiteSpeedLink(new Host.Endpoint { Port = 5000, Certificate = cert });", null)]
    [InlineData("Quic", "services.AddLiteSpeedLink(new Host.Endpoint { Port = 5000 });", "CS9035")]
    [InlineData("Local", "services.AddLiteSpeedLink(new Host.Endpoint { Name = \"probe\" });", null)]
    [InlineData("Memory", "services.AddLiteSpeedLink(new Host.Endpoint());", "CS9035")]
    [InlineData("Udp", "services.AddLiteSpeedLink(new Host.Endpoint { Port = 5000, MaxAmplification = 3 });", null)]
    public void RequiredMembersAreCheckedAtCompileTime(string transport, string use, string? error)
    {
        var r = Generate(transport, use);

        if (error is null) r.Errors.Should().BeEmpty();
        else r.Errors.Select(e => e.Id).Should().Contain(error);
    }

    /// <summary>TLS obligatorio (Mutual) -> Certificate required; Tcp por defecto -> opcional.</summary>
    [Fact]
    public void CertificateFollowsTheCompileTimeTlsMode()
    {
        HostOf(Generate("Tcp, Tls = TlsMode.Mutual", "")).Should().Contain("public required " + Cert + " Certificate { get; init; }");
        HostOf(Generate("Tcp", "")).Should().Contain("public " + Cert + "? Certificate { get; init; }");
    }

    /// <summary>Con [JsonSetting] el endpoint es opcional: en ejecucion, configuracion de la app si esta cargada y si no la del contenedor.</summary>
    [Fact]
    public void WithSettingsTheEndpointFallsBackToTheAppSettings()
    {
        var r = Generate("Tcp", "services.AddLiteSpeedLink(new Host.Endpoint());", """[JsonSetting<RemoteOptions>("Endpoint")]""");

        r.Errors.Should().BeEmpty();
        HostOf(r).Should().Contain("public int? Port { get; init; }")
            .And.Contain("Port ?? global::SourceCrafter.LiteSpeedLink.HostSettings.Port(host.HostServices, \"Endpoint\") ?? host.Settings.Port");
    }

    /// <summary>Generic Host real: arranca con la app (puerto de su configuracion), responde y deja de escuchar al parar.</summary>
    [Fact]
    public async Task RunsAsAHostedServiceOfAnyHost()
    {
        int port = FreePort();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection([new("Endpoint:Port", port.ToString())]);
        builder.Services.AddLiteSpeedLink(new DoublerEndpoint());

        using var app = builder.Build();
        await app.StartAsync();

        app.Services.GetRequiredService<Doubler>().HostServices!.GetService(typeof(IHostApplicationLifetime))
            .Should().BeSameAs(app.Services.GetRequiredService<IHostApplicationLifetime>());

        await using (var client = new TcpConnection(new IPEndPoint(IPAddress.Loopback, port)))
            (await client.GetAsync<int, int>(0, 21, new CancellationTokenSource(5000).Token)).Should().Be(42);

        await app.StopAsync();

        var connect = async () => { using var socket = new TcpClient(); await socket.ConnectAsync(IPAddress.Loopback, port); };
        await connect.Should().ThrowAsync<SocketException>();
    }

    /// <summary>Un puerto invalido en la configuracion de la app falla al arrancar, no en la primera peticion.</summary>
    [Fact]
    public async Task AnInvalidPortInTheAppSettingsFailsTheStart()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection([new("Endpoint:Port", "not-a-port")]);
        builder.Services.AddLiteSpeedLink(new DoublerEndpoint());

        using var app = builder.Build();
        var start = () => app.StartAsync();

        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Endpoint:Port*");
    }

    static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Lo que emite el generador para un host Tcp con [JsonSetting], sin el contenedor.</summary>
    public sealed class Doubler
    {
        public IServiceProvider? HostServices { get; init; }
    }

    readonly struct DoublerEndpoint : IHostedEndpoint<Doubler>
    {
        public int? Port { get; init; }

        public Doubler Create(IServiceProvider services) => new() { HostServices = services };

        public ValueTask<IAsyncDisposable> StartAsync(Doubler host, CancellationToken token)
            => new(new LocalHost(Server.StartTcpServer(Port ?? HostSettings.Port(host.HostServices, "Endpoint") ?? 0,
                (op, ctx, _) => ctx.ReturnAsync(ctx.Get<int>() * 2), () => { }, token: token)));
    }
}
