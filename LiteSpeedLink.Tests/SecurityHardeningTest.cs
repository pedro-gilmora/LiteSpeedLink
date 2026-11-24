using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;
using Xunit;

namespace LiteSpeedLink.Tests;

/// <summary>Revision de seguridad (rama security/hardening): un test por hallazgo corregido.</summary>
public class SecurityHardeningTest
{
    const string Cert = "global::System.Security.Cryptography.X509Certificates.X509Certificate2";

    static X509Certificate2 OtherCert()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Recargado desde PFX: SChannel no usa claves efimeras como certificado de cliente.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx, ""), "", X509KeyStorageFlags.Exportable);
    }

    static CancellationToken Within(int ms = 5000) => new CancellationTokenSource(ms).Token;

    /// <summary>Pinning por SHA-256 y modos de validacion fijados en compilacion.</summary>
    [Fact]
    public void ValidatorHonorsTheConfiguredCheck()
    {
        using var pinned = Constants.GetDevCert();
        using var other = OtherCert();

        var pinnedOnly = TlsPolicy.Validator(pinned, CertificateCheck.Pinned);
        pinnedOnly(this, pinned, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeTrue();
        pinnedOnly(this, other, null, SslPolicyErrors.None).Should().BeFalse();

        var chainOrPinned = TlsPolicy.Validator(pinned, CertificateCheck.ChainOrPinned);
        chainOrPinned(this, other, null, SslPolicyErrors.None).Should().BeTrue();
        chainOrPinned(this, other, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeFalse();

        TlsPolicy.Validator(null, CertificateCheck.Chain)(this, pinned, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeFalse();
    }

    /// <summary>Por defecto solo autentica el certificado configurado: una cadena valida de cualquier CA de la maquina (p. ej. publica) no basta.</summary>
    [Fact]
    public void TheDefaultCheckIsPinned()
    {
        using var cert = Constants.GetDevCert();
        using var other = OtherCert();

        default(CertificateCheck).Should().Be(CertificateCheck.Pinned);
        TlsPolicy.ForServer(cert, TlsMode.Mutual, default, System.Security.Authentication.SslProtocols.None)!
            .RemoteCertificateValidationCallback!(this, other, null, SslPolicyErrors.None).Should().BeFalse();
        HostOf(Generate("Tcp", ", Tls = TlsMode.Mutual")).Should().Contain("check: global::SourceCrafter.LiteSpeedLink.CertificateCheck.Pinned");
    }

    /// <summary>Hallazgo 1: el cliente QUIC aceptaba cualquier certificado; ahora valida el host y el host exige el del cliente.</summary>
    [Fact]
    [System.Runtime.Versioning.RequiresPreviewFeatures]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task QuicRejectsAnUnpinnedHostAndAClientWithoutCertificate()
    {
        if (!QuicListener.IsSupported) return;

        var cert = Constants.GetDevCert();
        await using var server = await Server.StartQuicServerAsync(0, (op, ctx, _) => ctx.ReturnAsync(1), () => { }, cert);
        var endpoint = new DnsEndPoint("localhost", server.LocalEndPoint.Port);

        using var other = OtherCert();
        await using (var wrongPin = endpoint.AsQuicConnection(other))
        {
            var call = async () => await wrongPin.GetAsync<int>(0, Within());
            await call.Should().ThrowAsync<Exception>();
        }

        await using var withoutClientCert = endpoint.AsQuicConnection(cert, tls: TlsMode.Server);
        var anonymous = async () => await withoutClientCert.GetAsync<int>(0, Within());
        await anonymous.Should().ThrowAsync<Exception>();
    }

    /// <summary>TLS solo del host (fijado en compilacion): el cliente no presenta certificado y valida el pin.</summary>
    [Fact]
    public async Task TcpServerModeAuthenticatesOnlyTheHost()
    {
        var cert = Constants.GetDevCert();
        using var server = Server.StartTcpServer(0, (op, ctx, _) => ctx.ReturnAsync(ctx.Get<int>() * 2), () => { }, cert, tls: TlsMode.Server, check: CertificateCheck.Pinned);
        var endpoint = new DnsEndPoint("localhost", ((IPEndPoint)server.LocalEndpoint).Port);

        await using (var pinned = endpoint.AsTcpConnection(cert, TlsMode.Server, CertificateCheck.Pinned))
            (await pinned.GetAsync<int, int>(0, 21, Within())).Should().Be(42);

        using var other = OtherCert();
        await using var wrongPin = endpoint.AsTcpConnection(other, TlsMode.Server, CertificateCheck.Pinned);
        var call = async () => await wrongPin.GetAsync<int, int>(0, 1, Within());
        await call.Should().ThrowAsync<Exception>();
    }

    /// <summary>Hallazgo 3: una trama a medias sin bytes nuevos cierra la conexion; una conexion ociosa no.</summary>
    [Fact]
    public async Task APartialFrameTimesOutButAnIdleConnectionDoesNot()
    {
        var input = new Pipe();
        var output = new Pipe();
        var serving = Server.ServePipeAsync(input.Reader, output.Writer, new DelegateRequestHandler((op, ctx, _) => ctx.ReturnAsync(0)), default, partialFrameTimeoutMs: 200);

        await Task.Delay(600);
        serving.IsCompleted.Should().BeFalse();

        var header = new byte[6];
        Framing.WriteLength(header, 100, Framing.LengthSize(100));
        await input.Writer.WriteAsync(header);

        await serving.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Plazo de lectura de un stream QUIC: un stream que se queda sin los 4 bytes del opId, o con ellos y parte del cuerpo pero sin FIN, se corta
    /// (antes retenia uno de los 256 huecos de la conexion hasta que el IdleTimeout de 5 min cerraba la conexion).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [System.Runtime.Versioning.RequiresPreviewFeatures]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task AStalledQuicStreamIsCutByTheReadDeadline(int bytes)
    {
        if (!QuicListener.IsSupported) return;

        var cert = Constants.GetDevCert();
        await using var server = await Server.StartQuicServerAsync(0, (op, ctx, _) => ctx.ReturnAsync(1), () => { }, cert, partialFrameTimeoutMs: 300, dataRateGraceMs: 300);
        var endpoint = new DnsEndPoint("localhost", server.LocalEndPoint.Port);

        await using var connection = await System.Net.Quic.QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            RemoteEndPoint = endpoint,
            DefaultStreamErrorCode = 0x0A,
            DefaultCloseErrorCode = 0x0B,
            ClientAuthenticationOptions = TlsPolicy.ForClient(endpoint, cert, TlsMode.Mutual, CertificateCheck.ChainOrPinned,
                System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13, [Constants.protocol])!
        });

        await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
        // opId 0 (no es el marcador del canal multiplexado) y sin FIN.
        await stream.WriteAsync(new byte[bytes]);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { await stream.ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (QuicException) { }

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    /// <summary>Hallazgo 3: con el limite de conexiones lleno, la siguiente espera en el backlog hasta que se libera una.</summary>
    [Fact]
    public async Task TheConnectionCapQueuesExtraConnections()
    {
        using var server = Server.StartTcpServer(0, (op, ctx, _) => ctx.ReturnAsync(ctx.Get<int>()), () => { }, maxConnections: 1);
        var endpoint = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)server.LocalEndpoint).Port);

        var first = new TcpConnection(endpoint);
        (await first.GetAsync<int, int>(0, 1, Within())).Should().Be(1);

        await using var second = new TcpConnection(endpoint);
        var pending = second.GetAsync<int, int>(0, 2, Within(10_000)).AsTask();

        await Task.Delay(500);
        pending.IsCompleted.Should().BeFalse();

        await first.DisposeAsync();
        (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(2);
    }

    /// <summary>Hallazgo 6: un corrId repetido con su stream vivo se rechaza sin pisar ni filtrar el CTS del primero, que sigue cancelable.</summary>
    [Fact]
    public async Task ADuplicateStreamIdIsRejected()
    {
        var input = new Pipe();
        var output = new Pipe();
        var serving = Server.ServePipeAsync(input.Reader, output.Writer, new DelegateRequestHandler((op, ctx, _) => ctx.EnumerateAsync(Forever())), default);

        await input.Writer.WriteAsync(TestFrames.Request(7, 1, []));
        await input.Writer.WriteAsync(TestFrames.Request(7, 1, []));

        var statuses = new List<ResponseStatus>();
        while (!statuses.Contains(ResponseStatus.Failed))
        {
            var result = await output.Reader.ReadAsync(Within());
            var buffer = result.Buffer;

            while (Framing.TryReadFrame(ref buffer, out int corrId, out var content))
            {
                corrId.Should().Be(7);
                statuses.Add(Framing.ReadStatus(content));
            }

            output.Reader.AdvanceTo(buffer.Start, buffer.End);
        }

        // Cancelacion del cliente: si el CTS del primero se hubiera pisado, su productor no pararia y el drenaje colgaria.
        await input.Writer.WriteAsync(TestFrames.Frame(7, []));
        await input.Writer.CompleteAsync();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        static async IAsyncEnumerable<int> Forever([EnumeratorCancellation] CancellationToken token = default)
        {
            yield return 1;
            await Task.Delay(Timeout.Infinite, token);
        }
    }

    /// <summary>Hallazgo 5: fuera de Windows el socket de Local vive en un directorio 0700; uno existente que no lo sea se rechaza.</summary>
    [Fact]
    public void LocalSocketDirectoryMustBePrivate()
    {
        if (OperatingSystem.IsWindows())
        {
            LocalSocket.GetPath("probe").Should().Be(Path.Combine(Path.GetTempPath(), "probe.lsl"));
            return;
        }

        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var ensure = typeof(LocalSocket).GetMethod("EnsurePrivate", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dir = Path.Combine(Path.GetTempPath(), "lsl-test-" + Guid.NewGuid().ToString("N"));

        try
        {
            ensure.Invoke(null, [dir]);
            File.GetUnixFileMode(dir).Should().Be(Private);

            // Propio con permisos abiertos: el chmod (que solo puede hacer el dueno) lo cierra a 0700.
            File.SetUnixFileMode(dir, Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            ensure.Invoke(null, [dir]);
            File.GetUnixFileMode(dir).Should().Be(Private);

            // Un enlace se rechaza antes del chmod (chmod lo seguiria).
            var link = dir + "-link";
            Directory.CreateSymbolicLink(link, dir);
            try
            {
                var viaLink = () => ensure.Invoke(null, [link]);
                viaLink.Should().Throw<TargetInvocationException>().WithInnerException<UnauthorizedAccessException>();
            }
            finally { File.Delete(link); }

            // Sin XDG_RUNTIME_DIR, nunca en el /tmp compartido (alli otro usuario podria crear o cambiar la ruta): el home del usuario.
            if (!Environment.IsPrivilegedProcess && Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is not { Length: > 0 })
                LocalSocket.GetPath("probe").Should().Be(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lsl", "probe.lsl"));
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    /// <summary>Revision del diff: una direccion por encima de su limite se corta al aceptar; las demas y la misma al liberar, entran.</summary>
    [Fact]
    public async Task ThePerAddressLimitClosesExtraConnectionsFromTheSameAddress()
    {
        using var server = Server.StartTcpServer(0, (op, ctx, _) => ctx.ReturnAsync(ctx.Get<int>()), () => { }, maxConnectionsPerAddress: 1);
        var endpoint = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)server.LocalEndpoint).Port);

        var first = new TcpConnection(endpoint);
        (await first.GetAsync<int, int>(0, 1, Within())).Should().Be(1);

        await using (var second = new TcpConnection(endpoint))
        {
            var call = async () => await second.GetAsync<int, int>(0, 2, Within());
            await call.Should().ThrowAsync<Exception>();
        }

        await first.DisposeAsync();
        await Task.Delay(200);

        await using var third = new TcpConnection(endpoint);
        (await third.GetAsync<int, int>(0, 3, Within())).Should().Be(3);
    }

    /// <summary>QUIC con los mismos topes que TCP: con Tls = Server los clientes no se autentican y sin tope podian abrir conexiones sin limite.</summary>
    [Fact]
    [System.Runtime.Versioning.RequiresPreviewFeatures]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task QuicHasThePerAddressLimitToo()
    {
        if (!QuicListener.IsSupported) return;

        var cert = Constants.GetDevCert();
        await using var server = await Server.StartQuicServerAsync(0, (op, ctx, _) => ctx.ReturnAsync(ctx.Get<int>()), () => { }, cert, maxConnectionsPerAddress: 1);
        var endpoint = new DnsEndPoint("localhost", server.LocalEndPoint.Port);

        var first = endpoint.AsQuicConnection(cert);
        (await first.GetAsync<int, int>(0, 1, Within())).Should().Be(1);

        await using (var second = endpoint.AsQuicConnection(cert))
        {
            var call = async () => await second.GetAsync<int, int>(0, 2, Within());
            await call.Should().ThrowAsync<Exception>();
        }

        await first.DisposeAsync();
        await Task.Delay(300);

        await using var third = endpoint.AsQuicConnection(cert);
        (await third.GetAsync<int, int>(0, 3, Within())).Should().Be(3);
    }

    /// <summary>Revision del diff: goteando bytes por debajo del plazo sin bytes, la trama a medias se corta por ritmo minimo (plazo total).</summary>
    [Fact]
    public async Task ATrickledFrameIsClosedByTheMinimumDataRate()
    {
        var trickled = await TrickleAsync(minDataRate: 1000);
        trickled.Should().BeLessThan(TimeSpan.FromSeconds(1.5));

        // Sin ritmo minimo, el mismo goteo (por debajo del plazo sin bytes) sigue abierto: el plazo total es lo que lo corta.
        var unlimited = await TrickleAsync(minDataRate: 0);
        unlimited.Should().BeGreaterThan(TimeSpan.FromSeconds(1.5));

        // Trama de 300 B: plazo total = 100 ms de gracia + 300 B / 1000 B/s = 400 ms; un byte cada 100 ms (< 1 s sin bytes).
        static async Task<TimeSpan> TrickleAsync(int minDataRate)
        {
            var input = new Pipe();
            var output = new Pipe();
            var serving = Server.ServePipeAsync(input.Reader, output.Writer, new DelegateRequestHandler((op, ctx, _) => ctx.ReturnAsync(0)), default,
                partialFrameTimeoutMs: 1000, minDataRate: minDataRate, dataRateGraceMs: 100);

            var header = new byte[Framing.LengthSize(300)];
            Framing.WriteLength(header, 300, header.Length);
            await input.Writer.WriteAsync(header);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!serving.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(2))
            {
                await Task.Delay(100);
                await input.Writer.WriteAsync(new byte[1]);
            }

            var elapsed = sw.Elapsed;
            await input.Writer.CompleteAsync();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
            return elapsed;
        }
    }

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

        [ServiceHost(ServiceConnectionType.TRANSPORT TLS)]
        [ServiceProvider]
        [Singleton<IContract, Contract>]
        public sealed partial class Host;

        [ServiceClient(ServiceConnectionType.TRANSPORT TLS)]
        [ServiceUnit<IContract>]
        [ServiceProvider]
        public sealed partial class Client;
        """;

    static GeneratorHarness.Result Generate(string transport, string tls = "", string? target = "net10.0-windows") => GeneratorHarness.Run(
        Source.Replace("TRANSPORT", transport).Replace("TLS", tls),
        target is null ? null : new Dictionary<string, string> { ["build_property.TargetFramework"] = target });

    static string HostOf(GeneratorHarness.Result r) => r.Sources.Single(s => s.Key.Contains("Host.host")).Value;

    static string ClientOf(GeneratorHarness.Result r) => r.Sources.Single(s => s.Key.Contains("Client.client")).Value;

    /// <summary>TLS fijado en compilacion: firma exacta (certificado obligatorio) y constantes para Kestrel.</summary>
    [Fact]
    public void MutualPinnedTcpIsFixedAtCompileTime()
    {
        var r = Generate("Tcp", ", Tls = TlsMode.Mutual, Check = CertificateCheck.Pinned");

        r.Errors.Should().BeEmpty();
        HostOf(r).Should().Contain(Cert + " certificate,").And.NotContain(Cert + "? certificate")
            .And.Contain("IHostedLiteSpeedLink.Tls => global::SourceCrafter.LiteSpeedLink.TlsMode.Mutual")
            .And.Contain("tls: global::SourceCrafter.LiteSpeedLink.TlsMode.Mutual, check: global::SourceCrafter.LiteSpeedLink.CertificateCheck.Pinned");
        ClientOf(r).Should().Contain("(string hostname, int port, " + Cert + " certificate)");
    }

    /// <summary>Solo el host con validacion por cadena: el cliente no recibe certificado.</summary>
    [Fact]
    public void ServerChainClientTakesNoCertificate()
    {
        var r = Generate("Tcp", ", Tls = TlsMode.Server, Check = CertificateCheck.Chain");

        r.Errors.Should().BeEmpty();
        ClientOf(r).Should().Contain("(string hostname, int port)")
            .And.Contain(".AsTcpConnection(tls: global::SourceCrafter.LiteSpeedLink.TlsMode.Server");
    }

    /// <summary>Hallazgo 1: el cliente QUIC generado recibe el certificado (pin y certificado de cliente).</summary>
    [Fact]
    public void QuicClientTakesTheCertificate()
    {
        var r = Generate("Quic");

        r.Errors.Should().BeEmpty();
        ClientOf(r).Should().Contain("(string hostname, int port, " + Cert + " certificate)")
            .And.Contain(".AsQuicConnection(certificate, tls: global::SourceCrafter.LiteSpeedLink.TlsMode.Default");
    }

    [Fact]
    public void QuicWithoutTlsIsRejected()
        => Generate("Quic", ", Tls = TlsMode.None").HasDiagnostic("SCLSL022").Should().BeTrue();

    /// <summary>Hallazgo 4: limite de amplificacion UDP fijado en compilacion; 3 por defecto (RFC 9000 8.1), 0 explicito = sin limite.</summary>
    [Theory]
    [InlineData("", "maxAmplification: 3", null)]
    [InlineData(", MaxAmplification = 0", "maxAmplification: 0", null)]
    [InlineData(", MaxAmplification = 10", "maxAmplification: 10", null)]
    [InlineData(", MaxAmplification = -1", null, "SCLSL025")]
    public void UdpMaxAmplificationIsFixedAtCompileTime(string amplification, string? emitted, string? error)
    {
        var r = GeneratorHarness.Run(Source.Replace("[ServiceHost(ServiceConnectionType.TRANSPORT TLS)]", "[ServiceHost(ServiceConnectionType.Udp" + amplification + ")]")
            .Replace("TRANSPORT", "Udp").Replace("TLS", ""), new Dictionary<string, string> { ["build_property.TargetFramework"] = "net10.0-windows" });

        if (error is not null)
        {
            r.HasDiagnostic(error).Should().BeTrue();
            return;
        }

        r.Errors.Should().BeEmpty();
        HostOf(r).Should().Contain(emitted).And.NotContain("int maxAmplification");
    }

    /// <summary>
    /// Por defecto (3x en el cable) una respuesta grande a una peticion pequena no sale: el cliente recibe un fallo corto al momento
    /// (no espera a su timeout) y la conexion sigue sirviendo respuestas que caben.
    /// </summary>
    [Fact]
    public async Task UdpReplyOverTheAmplificationLimitFailsFast()
    {
        using var udp = Server.StartUdpServer(0, (id, ctx, token) => id == 1
            ? ctx.EnumerateAsync(() => Enumerable.Repeat(new byte[9], 100))
            : ctx.ReturnAsync(new string('x', ctx.Get<int>())), () => { });
        using var connection = new DnsEndPoint("localhost", ((IPEndPoint)udp.Client.LocalEndPoint!).Port).AsUdpConnection();

        var tooLarge = async () => await connection.GetAsync<int, string>(0, 1000, Within(2000));
        (await tooLarge.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*MaxAmplification*");
        (await connection.GetAsync<int, string>(0, 10, Within(2000))).Should().HaveLength(10);

        // Un stream que se pasa a mitad termina con el error claro (el aviso), no por el timeout del cliente.
        var stream = async () => { await foreach (var _ in connection.EnumerateAsync<byte[]>(1, Within(2000))) { } };
        (await stream.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*MaxAmplification*");
    }

    /// <summary>Hallazgo 5: Local fuera de Windows deriva la ruta del directorio privado.</summary>
    [Fact]
    public void LocalOnLinuxUsesThePrivateSocketPath()
    {
        var r = Generate("Local", target: "net10.0-linux");

        r.Errors.Should().BeEmpty();
        HostOf(r).Should().Contain("global::SourceCrafter.LiteSpeedLink.LocalSocket.GetPath(name)");
        ClientOf(r).Should().Contain("global::SourceCrafter.LiteSpeedLink.LocalSocket.GetPath(name)");
    }
}
