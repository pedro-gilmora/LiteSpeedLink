using BenchmarkDotNet.Attributes;
using MemoryPack;
using SourceCrafter.LiteSpeedLink;

namespace LiteSpeedLink.Benchmarks.Scenarios;

/// <summary>
/// Procesamiento del request en el servidor: lectura del opId y deserialización del cuerpo.
/// </summary>
[MemoryDiagnoser]
public class ServerParsingBenchmarks
{
    private byte[] _request = [];

    [Params(PayloadSize.Small, PayloadSize.Medium)]
    public PayloadSize Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var body = MemoryPackSerializer.Serialize(Size == PayloadSize.Small ? "pedro" : Payloads.Medium);
        _request = new byte[Framing.OpIdSize + body.Length];
        Framing.WriteOpId(_request, Payloads.OpId);
        body.CopyTo(_request.AsSpan(Framing.OpIdSize));
    }

    /// <summary>Original: BitConverter + payload[8..] (copia el array completo).</summary>
    [Benchmark(Baseline = true)]
    public int Original_CopyBody()
    {
        long op = BitConverter.ToInt64(_request.AsSpan()[..8]);
        byte[] body = _request[8..];
        return (int)op ^ MemoryPackSerializer.Deserialize<string>(body)!.Length;
    }

    /// <summary>Actual: Framing + slice sin copia.</summary>
    [Benchmark]
    public int Current_SliceBody()
    {
        long op = Framing.ReadOpId(_request);
        ReadOnlyMemory<byte> body = _request.AsMemory(Framing.OpIdSize);
        return (int)op ^ MemoryPackSerializer.Deserialize<string>(body.Span)!.Length;
    }

    /// <summary>Original QUIC: el opId deserializado con MemoryPack.</summary>
    [Benchmark]
    public long Original_OpIdViaMemoryPack() => MemoryPackSerializer.Deserialize<long>(_request.AsSpan(0, 8));

    [Benchmark]
    public long Current_OpIdViaFraming() => Framing.ReadOpId(_request);
}
