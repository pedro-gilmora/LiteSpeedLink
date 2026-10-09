using System.Collections.Concurrent;

namespace SourceCrafter.LiteSpeedLink;

/// <summary>
/// Cache tipada clave -> valor con TTL absoluto y single-flight. [ClientCache]: clave de argumentos -> resultado final.
/// [ServerCache] con [CacheKey]: clave de argumentos -> bytes de la respuesta.
/// Tipos concretos fijados por el generador: sin boxing ni casts; el valor va inline en el nodo del diccionario.
/// La clave conserva los argumentos (el hash solo elige el bucket), asi que dos argumentos con el mismo hash no se confunden.
/// Un acierto devuelve la misma instancia a todos los llamantes. Clave por bytes (SCLSL018): <c>ReadOnlyMemory&lt;byte&gt;</c> con
/// <see cref="ByteContentComparer"/>; el generador la crea en un buffer propio por llamada, asi que no se copia.
/// </summary>
// ponytail: llena -> purga O(n) de expiradas y, si sigue llena, no guarda (sin LRU).
// ponytail: si el lider se cancela, los que esperan reciben la misma cancelacion (no reintentan).
public sealed class CacheManager<TKey, TValue>(int durationMs, int capacity, IEqualityComparer<TKey>? comparer = null) where TKey : notnull
{
    private readonly record struct Entry(TValue Value, long Expires);

    private readonly ConcurrentDictionary<TKey, Entry> _entries = new(comparer);
    private readonly ConcurrentDictionary<TKey, TaskCompletionSource<TValue>> _flights = new(comparer);
    // ConcurrentDictionary.Count toma todos los locks.
    private int _count;

    /// <summary>
    /// Single-flight tras un fallo de <see cref="TryGet"/>. true: este llamador lidera y debe terminar con
    /// <see cref="Complete"/> o <see cref="Fail"/>; false: esperar <paramref name="flight"/>.Task.
    /// </summary>
    public bool TryLead(TKey key, out TaskCompletionSource<TValue> flight)
    {
        if (_flights.TryGetValue(key, out flight!)) return false;

        var mine = new TaskCompletionSource<TValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        flight = _flights.GetOrAdd(key, mine);
        if (!ReferenceEquals(flight, mine)) return false;

        // Un lider anterior pudo publicar y salir entre el TryGet del llamador y este punto.
        if (!TryGet(key, out var value)) return true;

        _flights.TryRemove(new KeyValuePair<TKey, TaskCompletionSource<TValue>>(key, mine));
        mine.SetResult(value);
        return false;
    }

    public void Complete(TKey key, TaskCompletionSource<TValue> flight, TValue value)
    {
        Set(key, value);
        _flights.TryRemove(new KeyValuePair<TKey, TaskCompletionSource<TValue>>(key, flight));
        flight.TrySetResult(value);
    }

    /// <summary>
    /// Nada se cachea: los que esperan reciben el mismo fallo y la siguiente llamada vuelve a intentarlo.
    /// Idempotente (no-op tras <see cref="Complete"/>); sin <paramref name="error"/>, el lider termino sin resultado cacheable (rechazo).
    /// </summary>
    public void Fail(TKey key, TaskCompletionSource<TValue> flight, Exception? error)
    {
        if (flight.Task.IsCompleted) return;
        _flights.TryRemove(new KeyValuePair<TKey, TaskCompletionSource<TValue>>(key, flight));
        flight.TrySetException(error ?? new InvalidOperationException("The concurrent identical call didn't produce a cacheable result."));
    }

    public bool TryGet(TKey key, out TValue value)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.Expires > Environment.TickCount64)
            {
                value = entry.Value;
                return true;
            }

            Remove(key, entry);
        }

        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        var entry = new Entry(value, Environment.TickCount64 + durationMs);

        if (_entries.TryGetValue(key, out var old))
        {
            _entries.TryUpdate(key, entry, old);
            return;
        }

        if (Volatile.Read(ref _count) >= capacity && Purge() >= capacity) return;

        if (_entries.TryAdd(key, entry)) Interlocked.Increment(ref _count);
    }

    private int Purge()
    {
        long now = Environment.TickCount64;

        foreach (var (key, entry) in _entries)
            if (entry.Expires <= now) Remove(key, entry);

        return Volatile.Read(ref _count);
    }

    // Solo si sigue siendo la misma entrada: un Set concurrente pudo refrescarla.
    private void Remove(TKey key, Entry entry)
    {
        if (_entries.TryRemove(new KeyValuePair<TKey, Entry>(key, entry))) Interlocked.Decrement(ref _count);
    }
}
