using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using MemoryPack;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;

namespace LiteSpeedLink.Tests;

/// <summary>
/// Tramas compactas: longitud LEB128 de 1, 2 o 4 bytes, opId de 4 bytes (colisiones rechazadas en compilacion) y respuesta unaria de
/// exito sin longitud en QUIC dedicado (la delimita el FIN).
/// </summary>
public class CompactFramingTest
{
    static CancellationToken Within(int ms = 5000) => new CancellationTokenSource(ms).Token;

    [Theory]
    [InlineData(0, 1)]
    [InlineData(127, 1)]
    [InlineData(128, 2)]
    [InlineData(16383, 2)]
    [InlineData(16384, 4)]
    [InlineData(Framing.MaxFrameSize, 4)]
    public void TheLengthTakesOneTwoOrFourBytes(int length, int size)
    {
        Framing.LengthSize(length).Should().Be(size);

        var bytes = new byte[Framing.LengthPrefixSize];
        Framing.WriteLength(bytes, length, size);
        Framing.TryReadLength(new ReadOnlySequence<byte>(bytes), out int read, out int prefix).Should().BeTrue();
        (read, prefix).Should().Be((length, size));

        // Forma rellenada de 4 bytes (el escritor no pudo acortarla): misma longitud.
        Framing.WriteLength(bytes, length);
        Framing.TryReadLength(new ReadOnlySequence<byte>(bytes), out read, out prefix).Should().BeTrue();
        (read, prefix).Should().Be((length, Framing.LengthPrefixSize));
    }

    [Fact]
    public void AnInvalidOrIncompleteLengthIsDetected()
    {
        var fiveBytes = () => Framing.TryReadLength(new ReadOnlySequence<byte>([0x80, 0x80, 0x80, 0x80, 0x01]), out _, out _);
        fiveBytes.Should().Throw<InvalidDataException>();

        var tooLarge = new byte[Framing.LengthPrefixSize];
        Framing.WriteLength(tooLarge, Framing.MaxFrameSize + 1);
        var large = () => Framing.TryReadLength(new ReadOnlySequence<byte>(tooLarge), out _, out _);
        large.Should().Throw<InvalidDataException>();

        Framing.TryReadLength(new ReadOnlySequence<byte>([0x80]), out _, out _).Should().BeFalse();
    }

    /// <summary>
    /// Host: la trama se escribe sobre el hueco del pipe sin confirmar y la longitud queda en 1 o 2 bytes; un cuerpo que no cabe en el
    /// segmento se confirma segun llega y la longitud queda en la forma de 4 bytes. Las tres se leen igual.
    /// </summary>
    [Theory]
    [InlineData(10, 1)]
    [InlineData(1000, 2)]
    [InlineData(20_000, 4)]
    public async Task TheHostWritesTheShortestLengthItCan(int bodyLength, int prefix)
    {
        var pipe = new Pipe();
        var frames = new FrameWriter(pipe.Writer);
        var body = Enumerable.Range(0, bodyLength).Select(i => (byte)i).ToArray();

        frames.BeginFrame(9, Framing.StatusSize)[0] = (byte)ResponseStatus.Success;

        // En trozos, como el serializador.
        for (int offset = 0; offset < body.Length; offset += 700)
        {
            int n = Math.Min(700, body.Length - offset);
            body.AsSpan(offset, n).CopyTo(frames.GetSpan(n));
            frames.Advance(n);
        }

        frames.EndFrame();
        await pipe.Writer.FlushAsync();

        var buffer = (await pipe.Reader.ReadAtLeastAsync(prefix + Framing.CorrelationIdSize + Framing.StatusSize + bodyLength)).Buffer;
        buffer.Length.Should().Be(prefix + Framing.CorrelationIdSize + Framing.StatusSize + bodyLength);

        Framing.TryReadFrame(ref buffer, out int id, out var content).Should().BeTrue();
        id.Should().Be(9);
        Framing.ReadStatus(content).Should().Be(ResponseStatus.Success);
        content.Slice(Framing.StatusSize).ToArray().Should().Equal(body);
        buffer.IsEmpty.Should().BeTrue();
    }

    /// <summary>Cliente: <c>[len][corrId][opId:4][cuerpo]</c> con la longitud mas corta (el cuerpo se desplaza en su buffer contiguo).</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1000, 2)]
    [InlineData(20_000, 4)]
    public async Task TheClientWritesTheShortestLengthItCan(int bodyLength, int prefix)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serving = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync();
            var ns = connection.GetStream();
            var reader = PipeReader.Create(ns);

            while (true)
            {
                var result = await reader.ReadAsync(Within());
                var buffer = result.Buffer;

                if (Framing.TryReadLength(buffer, out int length, out int size) && buffer.Length >= size + length)
                {
                    size.Should().Be(prefix);
                    // corrId + opId (4) + byte[] de MemoryPack (longitud + bytes).
                    length.Should().Be(Framing.CorrelationIdSize + 4 + 4 + bodyLength);
                    Framing.TryReadFrame(ref buffer, out int id, out var content).Should().BeTrue();
                    Framing.ReadOpId(content).Should().Be(5);
                    var body = MemoryPackSerializer.Deserialize<byte[]>(content.Slice(Framing.OpIdSize).ToArray())!;
                    await ns.WriteAsync(TestFrames.Response(id, ResponseStatus.Success, MemoryPackSerializer.Serialize(body.Length)));
                    return;
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
            }
        });

        await using var client = new TcpConnection(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port));
        (await client.GetAsync<byte[], int>(5, new byte[bodyLength], Within())).Should().Be(bodyLength);
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// QUIC dedicado: la respuesta unaria de exito es <c>[Success][cuerpo]</c> hasta el FIN, sin longitud; un fallo sigue enmarcado (su primer
    /// byte, la longitud, nunca es 0). El cliente generado lee ambas.
    /// </summary>
    [Fact]
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    public async Task ADedicatedQuicUnaryResponseHasNoLength()
    {
        if (!QuicListener.IsSupported) return;

        var cert = Constants.GetDevCert();
        await using var server = await Server.StartQuicServerAsync(0, (op, ctx, _) => op == 0 ? ctx.ReturnAsync(ctx.Get<int>() + 1) : ctx.FailAsync("nope"), () => { }, cert);
        var endpoint = new DnsEndPoint("localhost", server.LocalEndPoint.Port);

        (await RoundTripAsync(0)).Should().Equal(new byte[] { (byte)ResponseStatus.Success }.Concat(MemoryPackSerializer.Serialize(42)));

        var failed = new ReadOnlySequence<byte>(await RoundTripAsync(1));
        failed.FirstSpan[0].Should().NotBe(0);
        Framing.TryReadFrame(ref failed, out var frame).Should().BeTrue();
        Framing.ReadStatus(frame).Should().Be(ResponseStatus.Failed);

        await using var client = endpoint.AsQuicConnection(cert, unaryStreams: 0);
        (await client.GetAsync<int, int>(0, 41, Within())).Should().Be(42);
        var call = async () => await client.GetAsync<int, int>(1, 0, Within());
        (await call.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*nope*");

        async Task<byte[]> RoundTripAsync(long op)
        {
            await using var connection = await System.Net.Quic.QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = endpoint,
                DefaultStreamErrorCode = 0x0A,
                DefaultCloseErrorCode = 0x0B,
                ClientAuthenticationOptions = TlsPolicy.ForClient(endpoint, cert, TlsMode.Mutual, CertificateCheck.Pinned,
                    System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13, [Constants.protocol])!
            });

            await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
            var request = new byte[Framing.OpIdSize].Concat(MemoryPackSerializer.Serialize(41)).ToArray();
            Framing.WriteOpId(request, op);
            await stream.WriteAsync(request, completeWrites: true);

            using var response = new MemoryStream();
            await stream.CopyToAsync(response).WaitAsync(TimeSpan.FromSeconds(5));
            return response.ToArray();
        }
    }

    const string Source = """
        using SourceCrafter.DependencyInjection.Attributes;
        using SourceCrafter.LiteSpeedLink;

        namespace Probe;

        public interface IContract : IServiceUnit
        {
            METHODS
        }

        public sealed class Contract : IContract
        {
            IMPLS
        }

        [ServiceHost(ServiceConnectionType.Tcp, Tls = TlsMode.None)]
        [ServiceProvider]
        [Singleton<IContract, Contract>]
        public sealed partial class Host;
        """;

    static GeneratorHarness.Result Generate(params string[] methods) => GeneratorHarness.Run(
        Source.Replace("METHODS", string.Concat(methods.Select(m => $"int {m}(int value);\n")))
            .Replace("IMPLS", string.Concat(methods.Select(m => $"public int {m}(int value) => value;\n"))),
        new Dictionary<string, string> { ["build_property.TargetFramework"] = "net10.0-windows" });

    static string HostOf(GeneratorHarness.Result r) => r.Sources.Single(s => s.Key.Contains("Host.host")).Value;

    static int Fnv32(string input)
    {
        uint hash = 2166136261;
        foreach (var b in Encoding.UTF8.GetBytes(input)) hash = unchecked((hash ^ b) * 16777619);
        return unchecked((int)hash);
    }

    /// <summary>Dos operaciones con el mismo id de 32 bits no compilan (SCLSL026) en vez de despachar una por otra en ejecucion.</summary>
    [Fact]
    public void ColludingOperationIdsAreRejectedAtCompileTime()
    {
        // La firma que se hashea es la que el host anota junto a cada id: de ahi la plantilla.
        var probe = Generate("Seed");
        probe.Errors.Should().BeEmpty();
        var arm = Regex.Match(HostOf(probe), @"(-?\d+) => __Seed_\w+\(__context, __token\), //Id for: \[(.+?)\]");
        arm.Success.Should().BeTrue();
        string signature = arm.Groups[2].Value;
        Fnv32(signature).Should().Be(int.Parse(arm.Groups[1].Value), "the test hashes what the generator hashes");

        // Cumpleanos en 32 bits: unas 65 mil firmas bastan para dar con dos iguales.
        Dictionary<int, string> seen = [];
        string? first = null, second = null;
        for (int i = 0; second is null; i++)
        {
            string name = "Op" + i;
            int id = Fnv32(signature.Replace("Seed", name));
            if (id == int.MinValue) continue;
            if (seen.TryGetValue(id, out var other)) (first, second) = (other, name);
            else seen[id] = name;
        }

        Generate(first!, second).HasDiagnostic("SCLSL026").Should().BeTrue();
        Generate(first!).Errors.Should().BeEmpty();
    }
}
