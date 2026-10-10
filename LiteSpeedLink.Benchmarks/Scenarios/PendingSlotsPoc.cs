using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;

namespace LiteSpeedLink.Benchmarks.Scenarios;

/// <summary>
/// Registro de llamadas en curso de <c>MultiplexedChannel</c>: hoy <see cref="ConcurrentDictionary{TKey, TValue}"/>
/// (un nodo por llamada, hash y candado por segmento); alternativa: array de slots indexado con <c>id &amp; Mask</c>
/// (CAS sin asignar). El slot puede estar ocupado por otra llamada con los mismos bits bajos: quien entrega
/// compara el id armado, igual que hoy <c>UnarySink.TrySet</c>/<c>StreamSink.TryWrite</c>.
/// <c>--filter *PendingSlotsPoc*</c>
/// </summary>
[MemoryDiagnoser]
public class PendingSlotsPoc
{
    private const int Ops = 4096;

    /// <summary>Llamadas en vuelo a la vez (ventana): 1 = secuencial; 64/1024 = concurrencia alta.</summary>
    [Params(1, 64, 1024)]
    public int InFlight { get; set; }

    private ConcurrentDictionary<int, object> _dict = null!;
    private PendingSlots<Sink> _slots = null!;
    private Sink[] _sinks = null!;
    private int[] _ids = null!;
    private int _nextId;

    [GlobalSetup]
    public void Setup()
    {
        PendingSlots<Sink>.SelfCheck();
        _dict = new();
        _slots = new();
        _sinks = [.. Enumerable.Range(0, InFlight).Select(_ => new Sink())];
        _ids = new int[InFlight];
        _nextId = 0;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Ops)]
    public int Dictionary()
    {
        int hits = 0, w = InFlight;
        for (int i = 0; i < Ops; i++)
        {
            int k = i % w;
            var sink = _sinks[k];
            if (i >= w) hits += Complete(_dict, _ids[k], sink);

            int id;
            do id = Interlocked.Increment(ref _nextId); while (id == 0);
            sink.Id = id;
            _dict[id] = sink;
            _ids[k] = id;
        }
        for (int k = 0; k < w; k++) hits += Complete(_dict, _ids[k], _sinks[k]);
        return hits;

        static int Complete(ConcurrentDictionary<int, object> d, int id, Sink sink)
        {
            int hit = d.TryGetValue(id, out var p) && ((Sink)p).Id == id ? 1 : 0;
            d.TryRemove(new(id, sink));
            return hit;
        }
    }

    [Benchmark(OperationsPerInvoke = Ops)]
    public int Slots()
    {
        int hits = 0, w = InFlight;
        for (int i = 0; i < Ops; i++)
        {
            int k = i % w;
            var sink = _sinks[k];
            if (i >= w) hits += Complete(_slots, _ids[k], sink);
            _ids[k] = _slots.Add(sink);
        }
        for (int k = 0; k < w; k++) hits += Complete(_slots, _ids[k], _sinks[k]);
        return hits;

        static int Complete(PendingSlots<Sink> s, int id, Sink sink)
        {
            int hit = s.Get(id) is { } p && p.Id == id ? 1 : 0;
            s.Remove(id, sink);
            return hit;
        }
    }

    /// <summary>Hilos en paralelo, cada uno registra/entrega/libera: mide el coste del CAS frente al candado por segmento.</summary>
    [Benchmark(OperationsPerInvoke = Ops * 8)]
    public int DictionaryParallel()
    {
        int hits = 0;
        System.Threading.Tasks.Parallel.For(0, 8, _ =>
        {
            var sink = new Sink();
            int h = 0;
            for (int i = 0; i < Ops; i++)
            {
                int id;
                do id = Interlocked.Increment(ref _nextId); while (id == 0);
                sink.Id = id;
                _dict[id] = sink;
                if (_dict.TryGetValue(id, out var p) && ReferenceEquals(p, sink)) h++;
                _dict.TryRemove(new(id, (object)sink));
            }
            Interlocked.Add(ref hits, h);
        });
        return hits;
    }

    [Benchmark(OperationsPerInvoke = Ops * 8)]
    public int SlotsParallel()
    {
        int hits = 0;
        System.Threading.Tasks.Parallel.For(0, 8, _ =>
        {
            var sink = new Sink();
            int h = 0;
            for (int i = 0; i < Ops; i++)
            {
                int id = _slots.Add(sink);
                if (_slots.Get(id) is { } p && p.Id == id) h++;
                _slots.Remove(id, sink);
            }
            Interlocked.Add(ref hits, h);
        });
        return hits;
    }

    internal sealed class Sink : PendingSlots<Sink>.IArmed
    {
        public int Id { get; set; }
    }
}

/// <summary>
/// Tabla fija de llamadas en curso: id = contador global (0 reservado); slot = <c>id &amp; Mask</c>.
/// Si el slot sigue ocupado (p. ej. un stream largo) se salta al id siguiente; tabla llena -> excepcion.
/// ponytail: tope de <see cref="Size"/> llamadas en vuelo por conexion, igual que MemChannel (4096).
/// </summary>
internal sealed class PendingSlots<T> where T : class, PendingSlots<T>.IArmed
{
    public interface IArmed { int Id { get; set; } }

    public const int Bits = 12, Size = 1 << Bits, Mask = Size - 1;

    private readonly T?[] _slots = new T?[Size];
    private int _next;

    public int Add(T item)
    {
        for (int tries = 0; tries <= Size; tries++)
        {
            int id = Interlocked.Increment(ref _next);
            if (id == 0) continue;

            // Armado antes de publicarlo: quien lo vea en el slot ya puede comparar el id.
            item.Id = id;
            if (Interlocked.CompareExchange(ref _slots[id & Mask], item, null) is null) return id;
        }

        throw new InvalidOperationException("Too many calls in flight.");
    }

    /// <summary>Ocupante del slot; puede ser otra llamada con los mismos bits bajos: el llamador compara <c>Id</c>.</summary>
    public T? Get(int id) => Volatile.Read(ref _slots[id & Mask]);

    public void Remove(int id, T item) => Interlocked.CompareExchange(ref _slots[id & Mask], null, item);

    public IEnumerable<T> Live()
    {
        foreach (var s in _slots) if (s is not null) yield return s;
    }

    /// <summary>Checks: entrega tardia descartada, slot ocupado saltado, tabla llena, concurrencia, Live.</summary>
    public static void SelfCheck()
    {
        static void Check(bool ok, string what)
        {
            if (!ok) throw new InvalidOperationException("PendingSlots: " + what);
        }

        var t = new PendingSlots<T>();
        T New() => (T)Activator.CreateInstance(typeof(T))!;

        // Entrega tardia: tras liberar, el id viejo no encuentra a nadie.
        var a = New();
        int ida = t.Add(a);
        t.Remove(ida, a);
        Check(t.Get(ida) is null, "stale id after remove");

        // Slot ocupado por una llamada larga: los ids que caen encima se saltan y no la pisan.
        var longCall = New();
        int idl = t.Add(longCall);
        var b = New();
        for (int i = 0; i < Size * 3; i++)
        {
            int id = t.Add(b);
            Check((id & Mask) != (idl & Mask), "occupied slot reused");
            Check(t.Get(id) == b && b.Id == id, "own slot");
            t.Remove(id, b);
        }
        Check(t.Get(idl) == longCall && longCall.Id == idl, "long call overwritten");

        // Respuesta tardia de un id viejo con los mismos bits bajos: el ocupante actual tiene otro Id.
        int reused = t.Add(b);
        t.Remove(reused, b);
        int again;
        do again = t.Add(b); while ((again & Mask) != (reused & Mask) && Remove(t, again, b));
        Check(t.Get(reused) is { } occ && occ.Id != reused, "stale id matched new occupant");
        t.Remove(again, b);
        t.Remove(idl, longCall);

        // Tabla llena.
        var all = Enumerable.Range(0, Size).Select(_ => New()).ToArray();
        foreach (var x in all) t.Add(x);
        Check(t.Live().Count() == Size, "live count");
        bool threw = false;
        try { t.Add(New()); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "full table must throw");
        foreach (var x in all) t.Remove(x.Id, x);
        Check(!t.Live().Any(), "empty after remove");

        // Concurrencia: cada hilo solo ve su propio sink bajo su id.
        int errors = 0;
        System.Threading.Tasks.Parallel.For(0, Environment.ProcessorCount * 2, _ =>
        {
            var own = new List<(int Id, T Item)>();
            for (int i = 0; i < 20_000; i++)
            {
                var item = New();
                int id = t.Add(item);
                own.Add((id, item));
                if (own.Count == 16)
                {
                    foreach (var (oid, o) in own)
                    {
                        if (t.Get(oid) != o || o.Id != oid) Interlocked.Increment(ref errors);
                        t.Remove(oid, o);
                    }
                    own.Clear();
                }
            }
            foreach (var (oid, o) in own) t.Remove(oid, o);
        });
        Check(errors == 0, $"{errors} cross-thread mismatches");
        Check(!t.Live().Any(), "leaked slots");

        static bool Remove(PendingSlots<T> t, int id, T item)
        {
            t.Remove(id, item);
            return true;
        }
    }
}
