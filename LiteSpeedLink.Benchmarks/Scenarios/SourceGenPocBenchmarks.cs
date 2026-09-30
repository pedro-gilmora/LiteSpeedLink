using System.Buffers;
using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using MemoryPack;

namespace LiteSpeedLink.Benchmarks.Scenarios;

/// <summary>
/// POC del estudio (docs/SourceGenStudy.md), sin red: forma actual (decide en runtime) vs forma que emitiria el generador.
/// #1 stream emitido, #3 host especializado (sin delegado), #6 framing como estrategia estatica.
/// </summary>
[MemoryDiagnoser]
public class SourceGenPocBenchmarks
{
    private const int Items = 1000;
    private readonly ArrayBufferWriter<byte> _out = new(64 * 1024);
    private bool _correlated = true;
    private int _batch = 64;
    private RuntimeHandler _handler = null!;

    [GlobalSetup]
    public void Setup() => _handler = RuntimeHost.Handle;

    // ---------- #6 framing: bool en runtime vs struct estatico ----------

    [Benchmark(Baseline = true)]
    public int Framing_RuntimeBool()
    {
        _out.ResetWrittenCount();
        for (int i = 0; i < Items; i++) RuntimeFraming.Write(_out, _correlated, i, 1, 4);
        return _out.WrittenCount;
    }

    [Benchmark]
    public int Framing_Generated()
    {
        _out.ResetWrittenCount();
        for (int i = 0; i < Items; i++) Framing<Correlated>.Write(_out, i, 1, 4);
        return _out.WrittenCount;
    }

    // ---------- #3 dispatch: delegado + switch vs host especializado ----------

    [Benchmark]
    public int Dispatch_Delegate()
    {
        int acc = 0;
        for (int i = 0; i < Items; i++) acc += _handler(RuntimeHost.Ops[i & 3], i);
        return acc;
    }

    [Benchmark]
    public int Dispatch_Generated()
    {
        int acc = 0;
        for (int i = 0; i < Items; i++) acc += Serve<GeneratedHost>(RuntimeHost.Ops[i & 3], i);
        return acc;
    }

    // ---------- #1 stream: Func + rama por item vs bucle emitido ----------

    [Benchmark]
    public async Task<int> Stream_Runtime()
    {
        _out.ResetWrittenCount();
        int count = Items;
        await new RuntimeStream(_out, _batch).EnumerateAsync(() => Enumerable.Range(0, count));
        return _out.WrittenCount;
    }

    [Benchmark]
    public int Stream_Generated()
    {
        _out.ResetWrittenCount();
        GeneratedStream.Range(_out, Items);
        return _out.WrittenCount;
    }

    // ---------- #1b politica: campo en runtime vs tipo (lo que emite [Stream]) ----------

    private readonly PolicyBatcher _batcher = new(new(64, long.MaxValue));

    [Benchmark]
    public int Policy_Runtime()
    {
        _out.ResetWrittenCount();
        for (int i = 0; i < Items; i++) _batcher.Add(_out, i);
        return _out.WrittenCount;
    }

    [Benchmark]
    public int Policy_Typed()
    {
        _out.ResetWrittenCount();
        for (int i = 0; i < Items; i++) _batcher.Add<Batch64>(_out, i);
        return _out.WrittenCount;
    }

    [Benchmark]
    public int Policy_RuntimeTimed()
    {
        _out.ResetWrittenCount();
        for (int i = 0; i < Items; i++) _batcher.AddTimed(_out, i);
        return _out.WrittenCount;
    }

    [Benchmark]
    public int Policy_TypedTimed()
    {
        _out.ResetWrittenCount();
        for (int i = 0; i < Items; i++) _batcher.Add<Batch64Timed>(_out, i);
        return _out.WrittenCount;
    }

    private static int Serve<THost>(long op, int arg) where THost : IHost => THost.Handle(op, arg);
}

// Replica de AddToBatchAsync (Server.Pipes.cs): politica leida de un campo vs argumento de tipo.
internal readonly record struct RtPolicy(int Items, long MaxDelayTicks);

internal readonly struct Batch64 : SourceCrafter.LiteSpeedLink.IStreamPolicy
{
    public static int Items => 64;
    public static long MaxDelayTicks => long.MaxValue;
}

internal readonly struct Batch64Timed : SourceCrafter.LiteSpeedLink.IStreamPolicy
{
    public static int Items => 64;
    public static long MaxDelayTicks => System.Diagnostics.Stopwatch.Frequency / 1000;
}

internal sealed class PolicyBatcher(RtPolicy policy)
{
    private readonly RtPolicy _timed = new(64, System.Diagnostics.Stopwatch.Frequency / 1000);
    private int _batched, _flushes;
    private long _started;

    public void Add(ArrayBufferWriter<byte> w, int item) => Add(w, item, policy);

    public void AddTimed(ArrayBufferWriter<byte> w, int item) => Add(w, item, _timed);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void Add(ArrayBufferWriter<byte> w, int item, RtPolicy p)
    {
        if (_batched == 0) _started = System.Diagnostics.Stopwatch.GetTimestamp();
        MemoryPackSerializer.Serialize(w, item);
        if (++_batched >= p.Items || w.WrittenCount >= 32 * 1024
            || System.Diagnostics.Stopwatch.GetTimestamp() - _started >= p.MaxDelayTicks) { _batched = 0; _flushes++; }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Add<TPolicy>(ArrayBufferWriter<byte> w, int item) where TPolicy : struct, SourceCrafter.LiteSpeedLink.IStreamPolicy
    {
        if (TPolicy.MaxDelayTicks != long.MaxValue && _batched == 0) _started = System.Diagnostics.Stopwatch.GetTimestamp();
        MemoryPackSerializer.Serialize(w, item);
        if (++_batched >= TPolicy.Items || w.WrittenCount >= 32 * 1024
            || (TPolicy.MaxDelayTicks != long.MaxValue && System.Diagnostics.Stopwatch.GetTimestamp() - _started >= TPolicy.MaxDelayTicks)) { _batched = 0; _flushes++; }
    }
}

// ===== Forma actual (runtime) =====

internal static class RuntimeFraming
{
    public static void Write(IBufferWriter<byte> w, bool correlated, int corrId, byte status, int len)
    {
        int header = correlated ? 9 : 5;
        var span = w.GetSpan(header);
        BinaryPrimitives.WriteInt32LittleEndian(span, len);
        if (correlated) BinaryPrimitives.WriteInt32LittleEndian(span[4..], corrId);
        span[header - 1] = status;
        w.Advance(header);
    }
}

internal delegate int RuntimeHandler(long op, int arg);

internal static class RuntimeHost
{
    public static readonly long[] Ops = [-512143187029202735, 2010337501524831397, -1546881052565485173, 77];

    public static int Handle(long op, int arg) => op switch
    {
        -512143187029202735 => arg + 1,
        2010337501524831397 => arg * 2,
        -1546881052565485173 => arg - 1,
        _ => 0,
    };
}

internal sealed class RuntimeStream(ArrayBufferWriter<byte> w, int batchItems)
{
    private int _batched;

    public async ValueTask EnumerateAsync(Func<IEnumerable<int>> value)
    {
        bool batched = batchItems > 0;
        foreach (var item in value()) await (batched ? Add(item) : Yield(item));
    }

    private ValueTask Add(int item)
    {
        MemoryPackSerializer.Serialize(w, item);
        if (++_batched >= batchItems) _batched = 0;
        return default;
    }

    private ValueTask Yield(int item)
    {
        MemoryPackSerializer.Serialize(w, item);
        return default;
    }
}

// ===== Forma generada =====

// Lo que se emitiria: el transporte fija la estrategia como argumento de tipo; el JIT elimina la rama.
internal interface IFraming
{
    static abstract int HeaderSize { get; }
    static abstract void WriteCorrelation(Span<byte> header, int corrId);
}

internal readonly struct Correlated : IFraming
{
    public static int HeaderSize => 9;
    public static void WriteCorrelation(Span<byte> header, int corrId) => BinaryPrimitives.WriteInt32LittleEndian(header[4..], corrId);
}

internal readonly struct Uncorrelated : IFraming
{
    public static int HeaderSize => 5;
    public static void WriteCorrelation(Span<byte> header, int corrId) { }
}

internal static class Framing<T> where T : struct, IFraming
{
    public static void Write(IBufferWriter<byte> w, int corrId, byte status, int len)
    {
        var span = w.GetSpan(T.HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(span, len);
        T.WriteCorrelation(span, corrId);
        span[T.HeaderSize - 1] = status;
        w.Advance(T.HeaderSize);
    }
}

// Lo que se emitiria por host: el switch vive en un metodo estatico que el transporte invoca sin delegado.
internal interface IHost
{
    static abstract int Handle(long op, int arg);
}

internal readonly struct GeneratedHost : IHost
{
    public static int Handle(long op, int arg) => op switch
    {
        -512143187029202735 => arg + 1,
        2010337501524831397 => arg * 2,
        -1546881052565485173 => arg - 1,
        _ => 0,
    };
}

// Lo que se emitiria por operacion de stream con [Stream(Batch = 64)]: sin Func, sin clausura, umbral constante.
internal static class GeneratedStream
{
    private const int Batch = 64;

    public static void Range(ArrayBufferWriter<byte> w, int count)
    {
        int batched = 0;
        for (int i = 0; i < count; i++)
        {
            MemoryPackSerializer.Serialize(w, i);
            if (++batched == Batch) batched = 0;
        }
    }
}
