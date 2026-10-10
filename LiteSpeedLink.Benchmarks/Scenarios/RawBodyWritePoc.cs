using BenchmarkDotNet.Attributes;
using MemoryPack;
using SourceCrafter.LiteSpeedLink;
using System.Buffers;

namespace LiteSpeedLink.Benchmarks.Scenarios;

/// <summary>
/// Cuerpo de peticion raw: hoy el cliente generado serializa en un <see cref="ArrayBufferWriter{T}"/> nuevo y el
/// transporte lo copia a su trama; la alternativa le pasa al transporte un escritor <c>static</c> con estado y
/// serializa directamente en la trama (como ya hace Memory con <c>MemoryRpc.CallAsync</c>).
/// </summary>
[MemoryDiagnoser]
public class RawBodyWritePoc
{
    private readonly ArrayBufferWriter<byte> _sink = new(64 * 1024);
    private FrameWriter _frames = null!;
    private string _user = null!, _pass = null!;

    [Params(8, 1024)]
    public int Chars { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _frames = new(_sink);
        _user = new string('u', Chars);
        _pass = new string('p', Chars);

        // Mismos bytes por ambos caminos.
        Buffered();
        var a = _sink.WrittenSpan.ToArray();
        Direct();
        if (!a.AsSpan().SequenceEqual(_sink.WrittenSpan)) throw new InvalidOperationException("Raw body mismatch");
        ViaPayload();
        if (!a.AsSpan().SequenceEqual(_sink.WrittenSpan)) throw new InvalidOperationException("Raw body mismatch");
    }

    [Benchmark(Baseline = true)]
    public int Buffered()
    {
        var body = Req(_user, _pass);
        _sink.ResetWrittenCount();
        Framing.WriteOpId(_frames.BeginFrame(1, Framing.OpIdSize), 42);
        _frames.Write(body.Span);
        _frames.EndFrame();
        return _sink.WrittenCount;
    }

    [Benchmark]
    public int Direct() => Send((_user, _pass), static (w, s) => Req(w, s.Item1, s.Item2));

    /// <summary>Por el camino generico del transporte (<c>TIn</c> = cuerpo struct con interfaz): el JIT debe quitar el box.</summary>
    [Benchmark]
    public int ViaPayload() => SendPayload(new Body<(string, string)>((_user, _pass), static (w, s) => Req(w, s.Item1, s.Item2)));

    private int SendPayload<TIn>(TIn payload)
    {
        _sink.ResetWrittenCount();
        Framing.WriteOpId(_frames.BeginFrame(1, Framing.OpIdSize), 42);
        if (payload is IBody) ((IBody)payload).WriteTo(_frames);
        else MemoryPackSerializer.Serialize(_frames, payload);
        _frames.EndFrame();
        return _sink.WrittenCount;
    }

    private interface IBody { void WriteTo(IBufferWriter<byte> writer); }

    private readonly struct Body<TState>(TState state, Action<IBufferWriter<byte>, TState> write) : IBody
    {
        public void WriteTo(IBufferWriter<byte> writer) => write(writer, state);
    }

    // Forma del transporte: el payload generico es el estado y el escritor sustituye a Serialize.
    private int Send<TState>(TState state, Action<IBufferWriter<byte>, TState> write)
    {
        _sink.ResetWrittenCount();
        Framing.WriteOpId(_frames.BeginFrame(1, Framing.OpIdSize), 42);
        write(_frames, state);
        _frames.EndFrame();
        return _sink.WrittenCount;
    }

    // Copia literal del escritor generado hoy.
    private static ReadOnlyMemory<byte> Req(string __p0, string __p1)
    {
        var __buf = new ArrayBufferWriter<byte>(16 + (__p0?.Length ?? 0) * 3 + (__p1?.Length ?? 0) * 3);
        using (var __s = MemoryPackWriterOptionalStatePool.Rent(null))
        {
            var __w = new MemoryPackWriter<ArrayBufferWriter<byte>>(ref __buf, __s);
            __w.WriteString(__p0);
            __w.WriteString(__p1);
            __w.Flush();
        }
        return __buf.WrittenMemory;
    }

    // Escritor propuesto: mismo cuerpo, sobre el IBufferWriter del transporte.
    private static void Req(IBufferWriter<byte> __out, string __p0, string __p1)
    {
        using var __s = MemoryPackWriterOptionalStatePool.Rent(null);
        var __w = new MemoryPackWriter<IBufferWriter<byte>>(ref __out, __s);
        __w.WriteString(__p0);
        __w.WriteString(__p1);
        __w.Flush();
    }
}
