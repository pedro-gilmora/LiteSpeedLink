using LiteSpeedLink.Benchmarks.Tcp;
using MemoryPack;
using SourceCrafter.LiteSpeedLink;

namespace LiteSpeedLink.Benchmarks;

/// <summary>
/// Verificacion semantica. Corre SIEMPRE antes de medir y, si falla, aborta: medir un camino
/// incorrecto produce cifras que no significan nada.
/// <para>
/// Todo lo que toca red lleva timeout. Un cliente roto no debe colgar la corrida: debe contar
/// como fallo y dejar que el resto de la verificacion termine.
/// </para>
/// </summary>
public static class SemanticCheck
{
    private static int _failures;

    public static int Run()
    {
        _failures = 0;

        Console.WriteLine("=== Verificacion semantica ===");
        Console.WriteLine();

        CheckFramingRoundTrip();
        CheckFramingEndianness();
        CheckTryReadFrame();
        CheckRequestLayoutsAgree();
        CheckTcpClients();

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? "TODO CORRECTO. Las cifras de esta corrida son comparables."
            : $"{_failures} FALLO(S). NO se debe publicar ninguna medicion de esta corrida.");

        return _failures == 0 ? 0 : 1;
    }

    private static void CheckFramingRoundTrip()
    {
        Span<byte> buffer = stackalloc byte[Framing.OpIdSize];

        foreach (var op in new[] { 0L, 1L, -1L, long.MaxValue, long.MinValue, Payloads.OpId })
        {
            Framing.WriteOpId(buffer, op);
            Expect($"opId ida y vuelta {op}", Framing.ReadOpId(buffer) == op);
        }
    }

    private static void CheckFramingEndianness()
    {
        Span<byte> buffer = stackalloc byte[Framing.OpIdSize];
        Framing.WriteOpId(buffer, 0x0102030405060708);

        Expect("opId little-endian explicito", buffer[0] == 0x08 && buffer[7] == 0x01);
    }

    private static void CheckTryReadFrame()
    {
        byte[] stream = [4, 0, 0, 0, 1, 2, 3, 4, 2, 0, 0, 0, 9, 9, 7];
        var seq = new System.Buffers.ReadOnlySequence<byte>(stream);

        Expect("trama 1 completa", Framing.TryReadFrame(ref seq, out var f1) && f1.Length == 4);
        Expect("trama 2 completa (coalescida)", Framing.TryReadFrame(ref seq, out var f2) && f2.Length == 2);
        Expect("trama 3 incompleta no se consume", !Framing.TryReadFrame(ref seq, out _) && seq.Length == 1);
    }

    private static void CheckRequestLayoutsAgree()
    {
        var body = MemoryPackSerializer.Serialize("pedro");

        var legacy = new byte[8 + body.Length];
        BitConverter.GetBytes(Payloads.OpId).CopyTo(legacy, 0);
        body.CopyTo(legacy, 8);

        var current = new byte[Framing.OpIdSize + body.Length];
        Framing.WriteOpId(current, Payloads.OpId);
        body.CopyTo(current.AsSpan(Framing.OpIdSize));

        // En x64 BitConverter es little-endian: ambos formatos deben ser identicos byte a byte, si
        // no la comparacion original vs actual del banco mediria dos protocolos distintos.
        Expect("layout original == actual (x64)", legacy.AsSpan().SequenceEqual(current));
    }

    private static void CheckTcpClients()
    {
        const int calls = 2000, parallelism = 32;

        var server = new EchoServer();

        Console.WriteLine();
        Console.WriteLine($"  TCP: {calls} llamadas, paralelismo {parallelism}, timeout 10 s");

        Report("A) sin proteccion (actual)", Probe(server, (s, t) => new UnsafeClient(s, t), calls, parallelism), expectCorrect: false);
        Report("B) SemaphoreSlim", Probe(server, (s, t) => new SerializedClient(s, t), calls, parallelism), expectCorrect: true);
        Report("C) multiplexado", Probe(server, (s, t) => new MultiplexedClient(s, t), calls, parallelism), expectCorrect: true);

        try { server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    private readonly record struct ProbeResult(int Ok, int Wrong, int Failed, int Hung);

    private static ProbeResult Probe(
        EchoServer server,
        Func<System.Net.Sockets.NetworkStream, System.Net.Sockets.TcpClient, IPocClient> factory,
        int calls,
        int parallelism)
    {
        var (stream, tcp) = PocClient.ConnectAsync(server.Port).GetAwaiter().GetResult();
        var client = factory(stream, tcp);

        int ok = 0, wrong = 0, failed = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var run = Parallel.ForAsync(0, calls, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, async (i, _) =>
        {
            var expected = "req-" + i;
            try
            {
                var actual = await client.CallAsync(expected, cts.Token).AsTask().WaitAsync(cts.Token);
                Interlocked.Increment(ref actual == expected ? ref ok : ref wrong);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _firstError, ex, null);
                Interlocked.Increment(ref failed);
            }
        });

        try { run.Wait(TimeSpan.FromSeconds(15)); } catch { }

        // Cerrar el socket desbloquea cualquier ReadAsync que siga pendiente.
        try { client.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }

        return new(ok, wrong, failed, calls - ok - wrong - failed);
    }

    private static Exception? _firstError;

    private static void Report(string name, ProbeResult r, bool expectCorrect)
    {
        var error = Interlocked.Exchange(ref _firstError, null);
        if (error is not null) Console.WriteLine($"         primera excepcion: {error.GetType().Name}: {error.Message.Split('\n')[0]}");

        var correct = r.Wrong == 0 && r.Failed == 0 && r.Hung == 0;
        var line = $"{name,-28} ok={r.Ok,5} ajena={r.Wrong,5} excepcion={r.Failed,5} colgada={r.Hung,5}";

        if (expectCorrect)
        {
            Expect(line, correct);
        }
        else
        {
            // El cliente actual es la hipotesis a refutar: se informa, no cuenta como fallo del banco.
            Console.WriteLine($"  [{(correct ? "?? " : "INFO")}] {line}{(correct ? "  (no reprodujo la carrera esta vez)" : "  <- carrera demostrada")}");
        }
    }

    private static void Expect(string name, bool condition)
    {
        Console.WriteLine($"  [{(condition ? " OK " : "FALLO")}] {name}");
        if (!condition) _failures++;
    }
}
