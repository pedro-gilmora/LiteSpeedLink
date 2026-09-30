using BenchmarkDotNet.Attributes;
using MemoryPack;
using SourceCrafter.LiteSpeedLink;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace LiteSpeedLink.Benchmarks.Scenarios;

/// <summary>
/// Respuestas de tamano fijo (unmanaged): MemoryPack sobre el FrameWriter vs escritura exacta
/// de <c>sizeof(T)</c> bytes. Mismo formato en el cable (MemoryPack escribe unmanaged como bytes crudos).
/// </summary>
[MemoryDiagnoser]
public class FixedSizeResponsePoc
{
    private readonly ArrayBufferWriter<byte> _buf = new(1024);
    private FrameWriter _frames = null!;
    private readonly (long, double) _value = (42, 3.5);

    [GlobalSetup]
    public void Setup() => _frames = new FrameWriter(_buf);

    [Benchmark(Baseline = true)]
    public int MemoryPack_Frame()
    {
        _buf.ResetWrittenCount();
        _frames.BeginFrame(1, 1)[0] = 0;
        MemoryPackSerializer.Serialize(_frames, _value);
        _frames.EndFrame();
        return _buf.WrittenCount;
    }

    [Benchmark]
    public int Exact_Frame()
    {
        _buf.ResetWrittenCount();
        _frames.BeginFrame(1, 1)[0] = 0;
        var span = _frames.GetSpan(Unsafe.SizeOf<(long, double)>());
        Unsafe.WriteUnaligned(ref span[0], _value);
        _frames.Advance(Unsafe.SizeOf<(long, double)>());
        _frames.EndFrame();
        return _buf.WrittenCount;
    }
}
