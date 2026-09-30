using BenchmarkDotNet.Attributes;
using MemoryPack;

namespace LiteSpeedLink.Benchmarks.Scenarios;

/// <summary>
/// 6d: decodificacion de un cuerpo de stream (items MemoryPack concatenados, como un Batch).
/// Generico por item (<c>Deserialize(span, ref item)</c>, lo que hace hoy el cliente) vs un unico
/// <see cref="MemoryPackReader"/> con metodos especificos (lo que emitiria raw).
/// </summary>
[MemoryDiagnoser]
public class StreamRawDecodePoc
{
    [Params(1, 64)]
    public int Items { get; set; }

    private byte[] _strings = null!, _packables = null!;

    [GlobalSetup]
    public void Setup()
    {
        _strings = [.. Enumerable.Range(0, Items).SelectMany(i => MemoryPackSerializer.Serialize($"item-{i:0000}"))];
        _packables = [.. Enumerable.Range(0, Items).SelectMany(i => MemoryPackSerializer.Serialize(new Credentials($"user-{i}", "secret")))];

        if (String_Generic() != String_Raw() || Packable_Generic() != Packable_Raw())
            throw new InvalidOperationException("Generic y Raw no decodifican lo mismo.");
    }

    [Benchmark(Baseline = true)]
    public int String_Generic()
    {
        int sum = 0;
        for (int offset = 0; offset < _strings.Length;)
        {
            string? item = null;
            offset += MemoryPackSerializer.Deserialize(_strings.AsSpan(offset), ref item);
            sum += item!.Length;
        }
        return sum;
    }

    [Benchmark]
    public int String_Raw()
    {
        int sum = 0;
        using var state = MemoryPackReaderOptionalStatePool.Rent(null);
        var r = new MemoryPackReader(_strings, state);
        while (r.Remaining > 0) sum += r.ReadString()!.Length;
        return sum;
    }

    [Benchmark]
    public int Packable_Generic()
    {
        int sum = 0;
        for (int offset = 0; offset < _packables.Length;)
        {
            Credentials item = default;
            offset += MemoryPackSerializer.Deserialize(_packables.AsSpan(offset), ref item);
            sum += item.User.Length;
        }
        return sum;
    }

    [Benchmark]
    public int Packable_Raw()
    {
        int sum = 0;
        using var state = MemoryPackReaderOptionalStatePool.Rent(null);
        var r = new MemoryPackReader(_packables, state);
        while (r.Remaining > 0) sum += r.ReadPackable<Credentials>().User.Length;
        return sum;
    }
}
