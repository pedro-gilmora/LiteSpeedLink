using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using FluentAssertions;
using MemoryPack;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;

namespace LiteSpeedLink.Tests;

/// <summary>Memoria por conexion: tamano maximo por operacion ([MaxMessageSize], 4 MB por defecto) y presupuesto de bytes en curso por conexion.</summary>
public class MessageSizeTest
{
    static byte[] Frame(int corrId, long op, int bodyLength, bool headerOnly = false)
    {
        var frame = new byte[Framing.FrameHeaderSize + Framing.OpIdSize + (headerOnly ? 0 : bodyLength)];
        Framing.WriteFrameHeader(frame, corrId, Framing.OpIdSize + bodyLength);
        Framing.WriteOpId(frame.AsSpan(Framing.FrameHeaderSize), op);
        for (int i = Framing.FrameHeaderSize + Framing.OpIdSize; i < frame.Length; i++) frame[i] = (byte)i;
        return frame;
    }

    /// <summary>Por encima del limite de la operacion la conexion se corta sin llamar al handler, completa o a medias (solo cabecera).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOversizeRequestClosesTheConnectionBeforeTheHandler(bool headerOnly)
    {
        var input = new Pipe();
        var output = new Pipe();
        int called = 0;
        var serving = Server.ServePipeAsync(input.Reader, output.Writer,
            new DelegateRequestHandler((op, ctx, _) => { Interlocked.Increment(ref called); return ctx.ReturnAsync(0); }, maxMessageSize: 100), default);

        await input.Writer.WriteAsync(Frame(1, 0, 101, headerOnly));

        // A medias no espera al cuerpo: con solo cabecera y opId ya sabe que no cabe.
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        called.Should().Be(0);
    }

    /// <summary>
    /// Un cuerpo mayor que el umbral de pausa del transporte se copia a su destino segun llega: con un pipe que deja de escribir al superar
    /// 1 KB sin consumir, un cuerpo de 100 KB llega entero (antes, dejando la trama sin consumir, el escritor y el lector se esperaban).
    /// </summary>
    [Fact]
    public async Task ABodyLargerThanTheTransportBufferArrivesWhole()
    {
        var input = new Pipe(new PipeOptions(pauseWriterThreshold: 1024, resumeWriterThreshold: 512));
        var output = new Pipe();
        var received = new TaskCompletionSource<byte[]>();
        var serving = Server.ServePipeAsync(input.Reader, output.Writer,
            new DelegateRequestHandler((op, ctx, _) => { received.TrySetResult(ctx.Body.ToArray()); return ctx.ReturnAsync(0); }), default);

        var frame = Frame(1, 0, 100_000);
        await input.Writer.WriteAsync(frame).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        var body = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        body.Should().Equal(frame.AsSpan(Framing.FrameHeaderSize + Framing.OpIdSize).ToArray());

        await input.Writer.CompleteAsync();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Presupuesto por conexion: con 600 B en curso y un presupuesto de 1000 B, la segunda peticion de 600 B espera a que termine la primera.</summary>
    [Fact]
    public async Task TheConnectionBudgetHoldsRequestsUntilEarlierOnesFinish()
    {
        var input = new Pipe();
        var output = new Pipe();
        var gate = new TaskCompletionSource();
        int started = 0;
        var serving = Server.ServePipeAsync(input.Reader, output.Writer,
            new DelegateRequestHandler(async (op, ctx, _) =>
            {
                Interlocked.Increment(ref started);
                await gate.Task;
                return await ctx.ReturnAsync(0);
            }), default, maxBufferedBytes: 1000);

        await input.Writer.WriteAsync(Frame(1, 0, 600));
        await input.Writer.WriteAsync(Frame(2, 0, 600));

        await Task.Delay(300);
        Volatile.Read(ref started).Should().Be(1);

        gate.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref started) < 2 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Volatile.Read(ref started).Should().Be(2);

        await input.Writer.CompleteAsync();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Con la conexion vacia se admite una peticion mayor que el presupuesto: sin interbloqueo.</summary>
    [Fact]
    public async Task AnEmptyConnectionAdmitsARequestLargerThanTheBudget()
    {
        var input = new Pipe();
        var output = new Pipe();
        var served = new TaskCompletionSource();
        var serving = Server.ServePipeAsync(input.Reader, output.Writer,
            new DelegateRequestHandler((op, ctx, _) => { served.TrySetResult(); return ctx.ReturnAsync(0); }), default, maxBufferedBytes: 100);

        await input.Writer.WriteAsync(Frame(1, 0, 5000));

        await served.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await input.Writer.CompleteAsync();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>El cliente rechaza la peticion que supera el limite antes de enviarla: falla esa llamada y la conexion sigue sirviendo.</summary>
    [Fact]
    public async Task TheClientRejectsAnOversizeRequestAndKeepsTheConnection()
    {
        using var server = Server.StartTcpServer(0, (op, ctx, _) => ctx.ReturnAsync(ctx.Get<byte[]>()!.Length), () => { });
        var endpoint = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)server.LocalEndpoint).Port);
        await using var client = new TcpConnection(endpoint, maxMessageSize: static op => op == 7 ? 64 : Framing.DefaultMaxMessageSize);

        var tooLarge = async () => await client.GetAsync<byte[], int>(7, new byte[100], new CancellationTokenSource(5000).Token);
        await tooLarge.Should().ThrowAsync<ArgumentException>().WithMessage("*exceeds the limit of operation 7*");

        (await client.GetAsync<byte[], int>(7, new byte[10], new CancellationTokenSource(5000).Token)).Should().Be(10);
        (await client.GetAsync<byte[], int>(8, new byte[100], new CancellationTokenSource(5000).Token)).Should().Be(100);
    }

    /// <summary>
    /// El host corta a mitad de un envio grande (aqui, un cliente sin limite contra un host de 100 B): falla la llamada y liberar el cliente
    /// no lanza. Antes, liberar intentaba volcar lo pendiente en el stream ya cerrado (ObjectDisposedException) y tumbaba el proceso.
    /// </summary>
    [Fact]
    public async Task DisposingAClientCutOffMidSendDoesNotThrow()
    {
        using var server = Server.StartTcpServer(0, new DelegateRequestHandler((op, ctx, _) => ctx.ReturnAsync(0), maxMessageSize: 100), () => { });
        var endpoint = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)server.LocalEndpoint).Port);
        var client = new TcpConnection(endpoint, maxMessageSize: static _ => Framing.MaxMessageSize);

        var cut = async () => await client.GetAsync<byte[], int>(1, new byte[8 * 1024 * 1024], new CancellationTokenSource(10_000).Token);
        await cut.Should().ThrowAsync<Exception>();

        var dispose = async () => await client.DisposeAsync();
        await dispose.Should().NotThrowAsync();
    }

    const string Source = """
        using SourceCrafter.DependencyInjection.Attributes;
        using SourceCrafter.LiteSpeedLink;

        namespace Probe;

        public interface IContract : IServiceUnit
        {
            ATTRIBUTE
            int Upload(byte[] data);

            int Echo(int value);
        }

        public sealed class Contract : IContract
        {
            public int Upload(byte[] data) => data.Length;
            public int Echo(int value) => value;
        }

        [ServiceHost(ServiceConnectionType.Tcp)]
        [ServiceProvider]
        [Singleton<IContract, Contract>]
        public sealed partial class Host;

        [ServiceClient(ServiceConnectionType.Tcp)]
        [ServiceUnit<IContract>]
        [ServiceProvider]
        public sealed partial class Client;
        """;

    static GeneratorHarness.Result Generate(string attribute) => GeneratorHarness.Run(Source.Replace("ATTRIBUTE", attribute));

    /// <summary>[MaxMessageSize] por operacion en compilacion: switch en host y cliente; sin el, ni switch ni consulta por llamada en el cliente.</summary>
    [Fact]
    public void PerOperationLimitsAreFixedAtCompileTime()
    {
        var r = Generate("[MaxMessageSize(64 * 1024 * 1024 / 8)]");

        r.Errors.Should().BeEmpty();
        r.Sources.Single(s => s.Key.Contains("Host.host")).Value.Should().Contain("__MaxMessageSize(long id) => id switch").And.Contain("=> 8388608, _ =>");
        r.Sources.Single(s => s.Key.Contains("Client.client")).Value.Should().Contain("maxMessageSize: static id => __MaxMessageSize(id)");

        var none = Generate("");
        none.Errors.Should().BeEmpty();
        none.Sources.Single(s => s.Key.Contains("Host.host")).Value.Should().Contain("__MaxMessageSize(long id) => global::SourceCrafter.LiteSpeedLink.Framing.DefaultMaxMessageSize;");
        none.Sources.Single(s => s.Key.Contains("Client.client")).Value.Should().NotContain("maxMessageSize:");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17 * 1024 * 1024)]
    public void AnInvalidLimitIsRejected(int bytes)
        => Generate($"[MaxMessageSize({bytes})]").HasDiagnostic("SCLSL023").Should().BeTrue();
}
