using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using FluentAssertions;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;

namespace LiteSpeedLink.Tests;

/// <summary>
/// Conexiones ociosas: el host cierra las que no tienen nada en curso con la trama de cierre <c>[len=4][corrId=0]</c>, y el cliente reconecta
/// en la siguiente llamada y reintenta (una vez por cierre) lo que el host no llego a procesar.
/// </summary>
public class IdleConnectionTest
{
    static CancellationToken Within(int ms = 5000) => new CancellationTokenSource(ms).Token;

    static async Task<byte[]> ReadFrameAsync(PipeReader reader)
    {
        while (true)
        {
            var result = await reader.ReadAsync(Within());
            var buffer = result.Buffer;

            if (Framing.TryReadFrame(ref buffer, out var frame))
            {
                var bytes = frame.ToArray();
                reader.AdvanceTo(buffer.Start);
                return bytes;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted) throw new EndOfStreamException();
        }
    }

    /// <summary>Sin nada en curso, el host cierra tras el plazo enviando antes la trama de cierre (corrId 0, sin cuerpo).</summary>
    [Fact]
    public async Task AnIdleConnectionIsClosedWithTheCloseFrame()
    {
        var input = new Pipe();
        var output = new Pipe();
        var serving = Server.ServePipeAsync(input.Reader, output.Writer, new DelegateRequestHandler((op, ctx, _) => ctx.ReturnAsync(0)), default, idleTimeoutMs: 200);

        var frame = await ReadFrameAsync(output.Reader);
        frame.Should().Equal(0, 0, 0, 0);

        await serving.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Un stream abierto (como una suscripcion) cuenta como en curso: la conexion no se cierra por ociosidad.</summary>
    [Fact]
    public async Task AnOpenStreamKeepsTheConnection()
    {
        var input = new Pipe();
        var output = new Pipe();
        var serving = Server.ServePipeAsync(input.Reader, output.Writer, new DelegateRequestHandler((op, ctx, _) => ctx.EnumerateAsync(Forever())), default, idleTimeoutMs: 200);

        var request = new byte[Framing.FrameHeaderSize + Framing.OpIdSize];
        Framing.WriteFrameHeader(request, 1, Framing.OpIdSize);
        await input.Writer.WriteAsync(request);

        await Task.Delay(800);
        serving.IsCompleted.Should().BeFalse();

        await input.Writer.CompleteAsync();

        static async IAsyncEnumerable<int> Forever([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
        {
            yield return 1;
            await Task.Delay(Timeout.Infinite, token);
        }
    }

    /// <summary>Tras el cierre por ociosidad, la siguiente llamada abre una conexion nueva sin error.</summary>
    [Fact]
    public async Task TheClientReconnectsAfterAnIdleClose()
    {
        using var server = Server.StartTcpServer(0, (op, ctx, _) => op == 0 ? ctx.ReturnAsync(ctx.Get<int>() + 1) : ctx.EnumerateAsync(Enumerable.Range(ctx.Get<int>() + 1, 1)), () => { }, idleTimeoutMs: 200);
        var endpoint = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)server.LocalEndpoint).Port);
        await using var client = new TcpConnection(endpoint);

        (await client.GetAsync<int, int>(0, 1, Within())).Should().Be(2);
        await Task.Delay(800);
        (await client.GetAsync<int, int>(0, 2, Within())).Should().Be(3);

        var items = new List<int>();
        await Task.Delay(800);
        await foreach (var i in client.EnumerateAsync<int, int>(1, 3, Within())) items.Add(i);
        items.Should().Equal(4);
    }

    /// <summary>
    /// Carrera real: la peticion sale a la vez que el host cierra. Un host de prueba responde a la primera peticion de cada conexion con la trama
    /// de cierre (sin procesarla, como el host al decidir ociosidad); el cliente la reintenta en otra conexion y obtiene la respuesta.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARequestCaughtByTheCloseFrameIsRetried(bool stream)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int accepted = 0;
        var serving = Task.Run(async () =>
        {
            // Primera conexion: lee la peticion y cierra con la trama de cierre. Segunda: host real.
            using (var first = await listener.AcceptTcpClientAsync())
            {
                Interlocked.Increment(ref accepted);
                var s = first.GetStream();
                var header = new byte[4];
                await s.ReadExactlyAsync(header);
                await s.ReadExactlyAsync(new byte[BinaryPrimitives.ReadInt32LittleEndian(header)]);
                var close = new byte[Framing.FrameHeaderSize];
                Framing.WriteFrameHeader(close, 0, 0);
                await s.WriteAsync(close);
            }

            using var second = await listener.AcceptTcpClientAsync();
            Interlocked.Increment(ref accepted);
            var ns = second.GetStream();
            await Server.ServePipeAsync(PipeReader.Create(ns), PipeWriter.Create(ns),
                new DelegateRequestHandler((op, ctx, _) => op == 0 ? ctx.ReturnAsync(ctx.Get<int>() * 10) : ctx.EnumerateAsync(Enumerable.Range(0, 3))), Within(10_000));
        });

        await using var client = new TcpConnection(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port));

        if (stream)
        {
            var items = new List<int>();
            await foreach (var i in client.EnumerateAsync<int>(1, Within())) items.Add(i);
            items.Should().Equal(0, 1, 2);
        }
        else
        {
            (await client.GetAsync<int, int>(0, 7, Within())).Should().Be(70);
        }

        Volatile.Read(ref accepted).Should().Be(2);
    }

    /// <summary>Conexion cortada sin trama de cierre (host reiniciado) y sin nada en curso: la siguiente llamada reconecta.</summary>
    [Fact]
    public async Task TheClientReconnectsAfterTheServerDropsAnIdleConnection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serving = Task.Run(async () =>
        {
            for (int n = 0; n < 2; n++)
            {
                using var connection = await listener.AcceptTcpClientAsync();
                var ns = connection.GetStream();
                // Atiende una peticion y corta la conexion (como un host que se reinicia).
                var input = PipeReader.Create(ns);
                var result = await input.ReadAsync();
                var buffer = result.Buffer;
                Framing.TryReadFrame(ref buffer, out var id, out var content).Should().BeTrue();
                var value = MemoryPack.MemoryPackSerializer.Deserialize<int>(content.Slice(Framing.OpIdSize).ToArray());
                var body = MemoryPack.MemoryPackSerializer.Serialize(value + 100);
                var response = new byte[Framing.FrameHeaderSize + Framing.StatusSize + body.Length];
                Framing.WriteFrameHeader(response, id, Framing.StatusSize + body.Length);
                response[Framing.FrameHeaderSize] = (byte)ResponseStatus.Success;
                body.CopyTo(response, Framing.FrameHeaderSize + Framing.StatusSize);
                await ns.WriteAsync(response);
                await ns.FlushAsync();
                await Task.Delay(100);
            }
        });

        await using var client = new TcpConnection(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port));

        (await client.GetAsync<int, int>(0, 1, Within())).Should().Be(101);
        await Task.Delay(500);
        (await client.GetAsync<int, int>(0, 2, Within())).Should().Be(102);
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>QUIC: tras perder la conexion (servidor reiniciado en el mismo puerto), la siguiente llamada reconecta. Antes fallaba para siempre.</summary>
    [Fact]
    [System.Runtime.Versioning.RequiresPreviewFeatures]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task TheQuicClientReconnectsAfterTheConnectionIsLost()
    {
        if (!QuicListener.IsSupported) return;

        var cert = Constants.GetDevCert();
        // Cancelar el token del host cierra sus conexiones (liberar solo el listener deja vivas las ya aceptadas).
        using var stop = new CancellationTokenSource();
        var first = await Server.StartQuicServerAsync(0, (op, ctx, _) => ctx.ReturnAsync(1), () => { }, cert, stop.Token);
        int port = first.LocalEndPoint.Port;
        await using var client = new DnsEndPoint("localhost", port).AsQuicConnection(cert);

        (await client.GetAsync<int>(0, Within())).Should().Be(1);

        stop.Cancel();
        await first.DisposeAsync();
        await Task.Delay(300);
        await using var second = await Server.StartQuicServerAsync(port, (op, ctx, _) => ctx.ReturnAsync(2), () => { }, cert);

        // La primera tras el corte puede fallar si estaba en vuelo cuando cayo; las siguientes reconectan.
        int value = 0;
        for (int attempt = 0; attempt < 5 && value != 2; attempt++)
        {
            try { value = await client.GetAsync<int>(0, Within()); }
            catch (Exception) { await Task.Delay(200); }
        }

        value.Should().Be(2);
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

        [ServiceHost(ServiceConnectionType.Tcp TLS)]
        [ServiceProvider]
        [Singleton<IContract, Contract>]
        public sealed partial class Host;
        """;

    /// <summary>TCP sin TLS explicito avisa (puede correr en claro y sin autenticar); Tls = None lo acepta explicitamente y Mutual no avisa.</summary>
    [Theory]
    [InlineData("", true)]
    [InlineData(", Tls = TlsMode.None", false)]
    [InlineData(", Tls = TlsMode.Mutual", false)]
    public void APlaintextTcpHostWarns(string tls, bool warns)
    {
        var r = GeneratorHarness.Run(Source.Replace("TLS", tls));

        r.Errors.Should().BeEmpty();
        r.HasDiagnostic("SCLSL024").Should().Be(warns);
    }
}
