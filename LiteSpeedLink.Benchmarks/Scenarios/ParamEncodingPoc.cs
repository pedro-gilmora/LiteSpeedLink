using BenchmarkDotNet.Attributes;
using MemoryPack;
using SourceCrafter.LiteSpeedLink;
using System.Buffers;

namespace LiteSpeedLink.Benchmarks.Scenarios;

/// <summary>
/// 5.1: peticion de varios parametros. Tupla generica (hoy) vs bloques contiguos con un unico
/// <see cref="MemoryPackWriter{TBufferWriter}"/>; lectura desde span vs desde secuencia segmentada;
/// y crecimiento de <see cref="ArrayBufferWriter{T}"/> (copia al reajustar) vs <see cref="SegmentedBufferWriter"/>.
/// </summary>
[MemoryDiagnoser]
public class ParamEncodingPoc
{
    private readonly ArrayBufferWriter<byte> _abw = new(256);
    private readonly SegmentedBufferWriter _seg = new();
    private byte[] _tuple = null!, _blocks = null!;
    private ReadOnlySequence<byte> _blocksSeq;
    private readonly string _name = "customer-0042";

    [Params(16, 65536)]
    public int Big { get; set; }

    private string _big = null!;

    [GlobalSetup]
    public void Setup()
    {
        _big = new string('x', Big);
        _tuple = MemoryPackSerializer.Serialize((42, _name, 3.5d, _big));
        var w = new ArrayBufferWriter<byte>();
        WriteBlocks(w);
        _blocks = w.WrittenSpan.ToArray();

        // Secuencia en trozos de 4 KB para forzar lecturas que cruzan segmentos.
        _seg.Reset();
        WriteBlocks(_seg);
        _blocksSeq = _seg.WrittenSequence;
    }

    private void WriteBlocks<TW>(TW buffer) where TW : class, IBufferWriter<byte>
    {
        using var state = MemoryPackWriterOptionalStatePool.Rent(null);
        var w = new MemoryPackWriter<TW>(ref buffer, state);
        w.WriteValue(42);
        w.WriteValue(_name);
        w.WriteValue(3.5d);
        w.WriteValue(_big);
        w.Flush();
    }

    // Estado alquilado una vez: aísla el coste del pool del de WriteValue/ReadValue.
    private readonly MemoryPackWriterOptionalState _wState = MemoryPackWriterOptionalStatePool.Rent(null);
    private readonly MemoryPackReaderOptionalState _rState = MemoryPackReaderOptionalStatePool.Rent(null);

    private void WriteBlocksTyped<TW>(TW buffer) where TW : class, IBufferWriter<byte>
    {
        var w = new MemoryPackWriter<TW>(ref buffer, _wState);
        w.WriteUnmanaged(42);
        w.WriteString(_name);
        w.WriteUnmanaged(3.5d);
        w.WriteString(_big);
        w.Flush();
    }

    [Benchmark]
    public int Write_Blocks_Typed()
    {
        _abw.ResetWrittenCount();
        WriteBlocksTyped(_abw);
        return _abw.WrittenCount;
    }

    [Benchmark]
    public int Read_Blocks_Typed()
    {
        var r = new MemoryPackReader(_blocks, _rState);
        r.ReadUnmanaged(out int a);
        var b = r.ReadString();
        r.ReadUnmanaged(out double c);
        var d = r.ReadString();
        return a + b!.Length + (int)c + d!.Length;
    }

    [Benchmark]
    public int Read_Blocks_Generic_CachedState()
    {
        var r = new MemoryPackReader(_blocks, _rState);
        int a = r.ReadValue<int>();
        var b = r.ReadValue<string>();
        var c = r.ReadValue<double>();
        var d = r.ReadValue<string>();
        return a + b!.Length + (int)c + d!.Length;
    }

    [Benchmark(Baseline = true)]
    public int Write_Tuple_Abw()
    {
        _abw.ResetWrittenCount();
        MemoryPackSerializer.Serialize(_abw, (42, _name, 3.5d, _big));
        return _abw.WrittenCount;
    }

    [Benchmark]
    public int Write_Blocks_Abw()
    {
        _abw.ResetWrittenCount();
        WriteBlocks(_abw);
        return _abw.WrittenCount;
    }

    [Benchmark]
    public long Write_Blocks_Segmented()
    {
        _seg.Reset();
        WriteBlocks(_seg);
        return _seg.WrittenCount;
    }

    /// <summary>ArrayBufferWriter nuevo por peticion: mide el coste de reajustar (copia) al crecer.</summary>
    [Benchmark]
    public int Write_Blocks_FreshAbw()
    {
        var w = new ArrayBufferWriter<byte>(256);
        WriteBlocks(w);
        return w.WrittenCount;
    }

    [Benchmark]
    public int Read_Tuple_Span() => MemoryPackSerializer.Deserialize<(int, string, double, string)>(_tuple).Item1;

    [Benchmark]
    public int Read_Blocks_Span()
    {
        using var state = MemoryPackReaderOptionalStatePool.Rent(null);
        var r = new MemoryPackReader(_blocks, state);
        int a = r.ReadValue<int>();
        var b = r.ReadValue<string>();
        var c = r.ReadValue<double>();
        var d = r.ReadValue<string>();
        return a + b!.Length + (int)c + d!.Length;
    }

    [Benchmark]
    public int Read_Blocks_Sequence()
    {
        using var state = MemoryPackReaderOptionalStatePool.Rent(null);
        var r = new MemoryPackReader(_blocksSeq, state);
        int a = r.ReadValue<int>();
        var b = r.ReadValue<string>();
        var c = r.ReadValue<double>();
        var d = r.ReadValue<string>();
        return a + b!.Length + (int)c + d!.Length;
    }
}
