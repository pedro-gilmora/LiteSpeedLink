using FluentAssertions;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;
using System.Buffers;
using System.Net;
using System.Reflection;
using System.Runtime.Versioning;
using Xunit;

namespace LiteSpeedLink.Tests;

/// <summary>Tests de items del plan que no tenian cobertura propia.</summary>
public partial class ServersTest
{
    /// <summary>2.4: opId en little-endian, identico por span y por secuencia (tambien partida).</summary>
    [Fact]
    public void TestFramingOpIdRoundtrip()
    {
        Span<byte> buffer = stackalloc byte[Framing.OpIdSize];
        Framing.WriteOpId(buffer, 0x0102030405060708);

        buffer[0].Should().Be(0x08);
        Framing.ReadOpId(buffer).Should().Be(0x0102030405060708);

        var bytes = buffer.ToArray();
        var first = new Segment(bytes.AsMemory(0, 3));
        var last = first.Append(bytes.AsMemory(3));

        Framing.ReadOpId(new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length)).Should().Be(0x0102030405060708);
    }

    /// <summary>3.3: una trama incompleta no se consume.</summary>
    [Fact]
    public void TestFramingIncompleteFrameIsNotConsumed()
    {
        byte[] partial = [10, 0, 0, 0, 1, 2];
        var sequence = new ReadOnlySequence<byte>(partial);

        Framing.TryReadFrame(ref sequence, out _).Should().BeFalse();
        sequence.Length.Should().Be(partial.Length);
    }

    /// <summary>3.1: Dispose no recrea el RpcBuffer.</summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void TestMemoryDisposeDoesNotResurrect()
    {
        string MmfName = $"Test-{Guid.CreateVersion7()}";

        using var server = Server.StartMemoryServer(MmfName, HandleMemoryRequest, () => { });
        var connection = new MemoryConnection(MmfName, 5000);
        var field = typeof(MemoryConnection).GetField("_rpc", BindingFlags.NonPublic | BindingFlags.Instance)!;

        connection.Dispose();
        field.GetValue(connection).Should().BeNull();

        connection.Get<(int, int, bool), int>(0, (1, 2, false)).Should().Be(3);
        connection.Dispose();
        connection.Dispose();

        field.GetValue(connection).Should().BeNull();
    }

    /// <summary>3.4: una excepcion del handler llega como Failed con el mensaje, sin la traza.</summary>
    [Fact]
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    public async Task TestTcpHandlerFailureReportsMessageOnly()
    {
        const int serverPort = 5006;

        using var server = Server.StartTcpServer(serverPort, (id, ctx, token) => throw new ArgumentException("boom"), () => { }, null, default);

        await using var connection = new DnsEndPoint("localhost", serverPort).AsTcpConnection();

        var act = async () => await connection.GetAsync<int, int>(0, 1);
        var error = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;

        error.Message.Should().Contain("boom").And.NotContain(" at ");

        // La conexion sigue viva tras el fallo.
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>3.4 en memoria: Failed y NotFound son distinguibles.</summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void TestMemoryHandlerFailureAndNotFound()
    {
        string MmfName = $"Test-{Guid.CreateVersion7()}";

        using var server = Server.StartMemoryServer(MmfName, (op, ctx, token) => op == 0 ? throw new ArgumentException("boom") : ResponseStatus.NotFound, () => { });
        using var connection = new MemoryConnection(MmfName, 5000);

        connection.Invoking(c => c.Get<int, int>(0, 1)).Should().Throw<InvalidOperationException>().WithMessage("*boom*");
        connection.Invoking(c => c.Get<int, int>(1, 1)).Should().Throw<NotImplementedException>();
    }

    /// <summary>5.5: el servidor Memory ya no recibe un timeout muerto; onFinalize se invoca al liberarlo.</summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void TestMemoryServerDisposeRunsOnFinalize()
    {
        int finalized = 0;
        var server = Server.StartMemoryServer($"Test-{Guid.CreateVersion7()}", (op, ctx, token) => ResponseStatus.NotFound, () => finalized++);

        server.Dispose();
        finalized.Should().Be(1);
    }

    /// <summary>3.7: QUIC con un stream por RPC, muchas llamadas en vuelo sobre una conexion.</summary>
    [Fact]
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    public async Task TestQuicConcurrentRoundtrips()
    {
        const int serverPort = 5007;
        var cert = Constants.GetDevCert();

        await using var server = await Server.StartQuicServerAsync(serverPort, HandleRequestAsync, () => { }, cert, default);
        await using var connection = new DnsEndPoint("localhost", serverPort).AsQuicConnection(cert);

        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(n => connection.GetAsync<(int, int, bool), int>(0, (n, n, false)).AsTask()));

        results.Should().Equal(Enumerable.Range(0, 100).Select(n => n * 2));
    }

    /// <summary>Optimizacion QUIC: cerrar la llamada drena el FIN y no aborta el stream (sin QuicException por llamada).</summary>
    [Fact]
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    public async Task TestQuicCallDoesNotThrowInternally()
    {
        const int serverPort = 5017;
        var cert = Constants.GetDevCert();

        await using var server = await Server.StartQuicServerAsync(serverPort, HandleRequestAsync, () => { }, cert, default);
        await using var connection = new DnsEndPoint("localhost", serverPort).AsQuicConnection(cert, unaryStreams: 0);
        await connection.GetAsync<(int, int, bool), int>(0, (1, 1, false));

        int quicExceptions = 0;
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> h = (_, e) => { if (e.Exception is System.Net.Quic.QuicException) Interlocked.Increment(ref quicExceptions); };
        AppDomain.CurrentDomain.FirstChanceException += h;
        try
        {
            for (int i = 0; i < 50; i++) (await connection.GetAsync<(int, int, bool), int>(0, (i, i, false))).Should().Be(i * 2);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= h; }

        quicExceptions.Should().Be(0);
    }

    /// <summary>Pool QUIC: unarias concurrentes multiplexadas en streams reutilizables; modo por-stream (0) sigue valido.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    public async Task TestQuicUnaryStreams(int unaryStreams)
    {
        int serverPort = 5020 + unaryStreams;
        var cert = Constants.GetDevCert();

        await using var server = await Server.StartQuicServerAsync(serverPort, HandleRequestAsync, () => { }, cert, default);
        await using var connection = new DnsEndPoint("localhost", serverPort).AsQuicConnection(cert, unaryStreams);

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(i => connection.GetAsync<(int, int, bool), int>(0, (i, i, false)).AsTask()));
        results.Should().Equal(Enumerable.Range(0, 200).Select(i => i * 2));
    }

    /// <summary>5.4: certificado autofirmado utilizable para TLS y estable entre llamadas.</summary>
    [Fact]
    public void TestDevCert()
    {
        using var cert = Constants.GetDevCert();
        using var again = Constants.GetDevCert();

        cert.HasPrivateKey.Should().BeTrue();
        cert.Subject.Should().Be("CN=localhost");
        again.Thumbprint.Should().Be(cert.Thumbprint);
    }

    /// <summary>3.8: por encima del limite por endpoint UDP los datagramas se descartan sin invocar el handler.</summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task TestUdpInFlightLimit()
    {
        const int serverPort = 5008;
        int invoked = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var server = Server.StartUdpServer(serverPort, async (id, ctx, token) =>
        {
            Interlocked.Increment(ref invoked);
            await gate.Task;
            return await ctx.ReturnAsync(0);
        }, () => { });

        using var client = new System.Net.Sockets.UdpClient();
        client.Connect("127.0.0.1", serverPort);

        var datagram = new byte[Framing.CorrelationIdSize + Framing.OpIdSize];
        for (int i = 0; i < Server.MaxInFlightPerEndpoint + 50; i++)
            await client.SendAsync(datagram);

        await Task.Delay(500);
        gate.SetResult();

        invoked.Should().Be(Server.MaxInFlightPerEndpoint);
    }

    /// <summary>Limites personalizados: UDP descarta por encima de N; TCP nunca supera N en paralelo.</summary>
    [Fact]
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    public async Task TestCustomInFlightLimits()
    {
        const int limit = 4;
        int invoked = 0, current = 0, peak = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var udp = Server.StartUdpServer(5009, async (id, ctx, token) =>
        {
            Interlocked.Increment(ref invoked);
            await gate.Task;
            return await ctx.ReturnAsync(0);
        }, () => { }, default, maxInFlightPerEndpoint: limit);

        using var client = new System.Net.Sockets.UdpClient();
        client.Connect("127.0.0.1", 5009);
        var datagram = new byte[Framing.CorrelationIdSize + Framing.OpIdSize];
        for (int i = 0; i < limit + 10; i++) await client.SendAsync(datagram);

        await Task.Delay(300);
        gate.SetResult();
        invoked.Should().Be(limit);

        using var tcp = Server.StartTcpServer(5010, async (id, ctx, token) =>
        {
            int now = Interlocked.Increment(ref current);
            for (int seen = peak; now > seen && Interlocked.CompareExchange(ref peak, now, seen) != seen; seen = peak) ;
            await Task.Delay(20, token);
            Interlocked.Decrement(ref current);
            return await ctx.ReturnAsync(ctx.Get<int>());
        }, () => { }, null, default, maxInFlightPerConnection: limit);

        await using var connection = new DnsEndPoint("localhost", 5010).AsTcpConnection();
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(n => connection.GetAsync<int, int>(0, n).AsTask()));

        results.Should().Equal(Enumerable.Range(0, 40));
        peak.Should().BeLessThanOrEqualTo(limit);

        var invalid = () => Server.StartUdpServer(5011, (id, ctx, token) => ctx.NotFoundAsync(), () => { }, default, 0);
        invalid.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>TCP sobre TLS (SslStream) con el certificado de desarrollo.</summary>
    [Fact]
    public async Task TestTcpTls()
    {
        using var cert = Constants.GetDevCert();
        using var tcp = Server.StartTcpServer(5012, (id, ctx, token) => ctx.ReturnAsync(ctx.Get<int>() * 2), () => { }, cert);

        await using var connection = new DnsEndPoint("localhost", 5012).AsTcpConnection(cert);
        (await connection.GetAsync<int, int>(0, 21)).Should().Be(42);
    }

    /// <summary>Anti-amplificacion UDP: con factor 1, una respuesta mayor que la peticion no se envia.</summary>
    [Fact]
    public async Task TestUdpAmplificationBudget()
    {
        using var udp = Server.StartUdpServer(5013, (id, ctx, token) =>
            ctx.ReturnAsync(new string('x', ctx.Get<int>())), () => { }, default, maxAmplification: 1);

        using var client = new System.Net.Sockets.UdpClient();
        client.Connect("127.0.0.1", 5013);

        async Task<bool> Answered(int size)
        {
            var datagram = new byte[Framing.CorrelationIdSize + Framing.OpIdSize].Concat(MemoryPack.MemoryPackSerializer.Serialize(size)).ToArray();
            await client.SendAsync(datagram);
            using var cts = new CancellationTokenSource(500);
            try { await client.ReceiveAsync(cts.Token); return true; }
            catch (OperationCanceledException) { return false; }
        }

        (await Answered(1)).Should().BeTrue();      // respuesta pequena: cabe en el presupuesto
        (await Answered(1000)).Should().BeFalse();  // 1000 bytes por ~12 recibidos: descartada
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
