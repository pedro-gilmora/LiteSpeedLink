using System.Buffers;
using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using MemoryPack;
using SourceCrafter.LiteSpeedLink;

namespace LiteSpeedLink.Benchmarks.Scenarios;

/// <summary>
/// Construcción del request en el cliente: cabecera opId + cuerpo MemoryPack.
/// Compara la implementación original, la actual y una candidata sin asignaciones.
/// </summary>
[MemoryDiagnoser]
public class RequestBuildingBenchmarks
{
    private readonly ArrayBufferWriter<byte> _writer = new(1024);

    [Params(PayloadSize.Small, PayloadSize.Medium)]
    public PayloadSize Size { get; set; }

    private string _payload = "";

    [GlobalSetup]
    public void Setup() => _payload = Size == PayloadSize.Small ? "pedro" : Payloads.Medium;

    /// <summary>Original de MemoryConnection: Serialize + new byte[] + BitConverter.GetBytes.</summary>
    [Benchmark(Baseline = true)]
    public int Original_BitConverter()
    {
        ReadOnlySpan<byte> body = MemoryPackSerializer.Serialize(_payload);
        Memory<byte> request = new byte[body.Length + 8];
        BitConverter.GetBytes(Payloads.OpId).CopyTo(request);
        body.CopyTo(request.Span[8..]);
        return request.Length;
    }

    /// <summary>Original de TCP/QUIC: MemoryPack para el opId + stackalloc + ToArray.</summary>
    [Benchmark]
    public int Original_StackallocToArray()
    {
        Span<byte> op = MemoryPackSerializer.Serialize(Payloads.OpId);
        Span<byte> body = MemoryPackSerializer.Serialize(_payload);
        Span<byte> result = stackalloc byte[op.Length + body.Length];
        op.CopyTo(result);
        body.CopyTo(result[op.Length..]);
        return result.ToArray().Length;
    }

    /// <summary>Actual: Framing + un único array final.</summary>
    [Benchmark]
    public int Current_Framing()
    {
        ReadOnlySpan<byte> body = MemoryPackSerializer.Serialize(_payload);
        var request = new byte[Framing.OpIdSize + body.Length];
        Framing.WriteOpId(request, Payloads.OpId);
        body.CopyTo(request.AsSpan(Framing.OpIdSize));
        return request.Length;
    }

    /// <summary>
    /// Candidata: MemoryPack serializa directamente tras la cabecera en un IBufferWriter reutilizado.
    /// Es lo que habilitaría un RpcBuffer/PipeWriter que exponga su destino.
    /// </summary>
    [Benchmark]
    public int Candidate_BufferWriter()
    {
        _writer.ResetWrittenCount();
        Framing.WriteOpId(_writer.GetSpan(Framing.OpIdSize), Payloads.OpId);
        _writer.Advance(Framing.OpIdSize);
        MemoryPackSerializer.Serialize(_writer, _payload);
        return _writer.WrittenCount;
    }

    /// <summary>Candidata TCP: prefijo de longitud reservado y rellenado después (framing 3.3).</summary>
    [Benchmark]
    public int Candidate_BufferWriter_LengthPrefixed()
    {
        _writer.ResetWrittenCount();
        var header = _writer.GetSpan(Framing.LengthPrefixSize + Framing.OpIdSize);
        Framing.WriteOpId(header[Framing.LengthPrefixSize..], Payloads.OpId);
        _writer.Advance(Framing.LengthPrefixSize + Framing.OpIdSize);
        MemoryPackSerializer.Serialize(_writer, _payload);

        var written = _writer.WrittenCount;
        // ArrayBufferWriter permite reescribir lo ya escrito vía WrittenMemory (el PipeWriter real
        // requiere calcular la longitud antes o serializar a un scratch; ver informe).
        BinaryPrimitives.WriteInt32LittleEndian(
            System.Runtime.InteropServices.MemoryMarshal.AsMemory(_writer.WrittenMemory).Span,
            written - Framing.LengthPrefixSize);
        return written;
    }
}

public enum PayloadSize { Small, Medium }
