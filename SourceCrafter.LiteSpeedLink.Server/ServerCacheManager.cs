using System.Collections.Concurrent;

namespace SourceCrafter.LiteSpeedLink;

/// <summary>
/// Cache de [ServerCache]: bytes de la peticion -> bytes de la respuesta ya procesada, con TTL absoluto.
/// La consulta va por el span del cuerpo (sin copiar la clave); solo al guardar se copian clave y respuesta,
/// porque el cuerpo vive en un buffer del pool.
/// </summary>
// ponytail: llena -> purga O(n) de expiradas y, si sigue llena, no guarda (sin LRU).
// ponytail: si el lider se cancela, los que esperan reciben la misma cancelacion (no reintentan).
public sealed class ServerCacheManager
{
    private readonly record struct Entry(ReadOnlyMemory<byte> Response, long Expires);

    private readonly ConcurrentDictionary<ReadOnlyMemory<byte>, Entry> _entries = new(ByteContentComparer.Instance);
    private readonly ConcurrentDictionary<ReadOnlyMemory<byte>, Entry>.AlternateLookup<ReadOnlySpan<byte>> _lookup;
    private readonly ConcurrentDictionary<ReadOnlyMemory<byte>, TaskCompletionSource<ReadOnlyMemory<byte>>>.AlternateLookup<ReadOnlySpan<byte>> _flights =
        new ConcurrentDictionary<ReadOnlyMemory<byte>, TaskCompletionSource<ReadOnlyMemory<byte>>>(ByteContentComparer.Instance)
            .GetAlternateLookup<ReadOnlySpan<byte>>();
    private readonly int _durationMs, _capacity;
    // ConcurrentDictionary.Count toma todos los locks.
    private int _count;

    public ServerCacheManager(int durationMs, int capacity)
    {
        _durationMs = durationMs;
        _capacity = capacity;
        _lookup = _entries.GetAlternateLookup<ReadOnlySpan<byte>>();
    }

    /// <summary>
    /// Single-flight tras un fallo de <see cref="TryGet"/>. true: este llamador lidera y debe terminar con
    /// <see cref="Complete"/> o <see cref="Fail"/>; false: esperar <paramref name="flight"/>.Task.
    /// La clave en curso se copia (el cuerpo vuelve al pool tras el dispatch).
    /// </summary>
    public bool TryLead(ReadOnlyMemory<byte> request, out TaskCompletionSource<ReadOnlyMemory<byte>> flight)
    {
        TaskCompletionSource<ReadOnlyMemory<byte>>? mine = null;

        while (!_flights.TryGetValue(request.Span, out flight!))
        {
            mine ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_flights.TryAdd(request.Span, mine)) continue;

            flight = mine;
            // Un lider anterior pudo publicar y salir entre el TryGet del llamador y este punto.
            if (!TryGet(request, out var response)) return true;

            Land(request, mine);
            mine.SetResult(response);
            return false;
        }

        return false;
    }

    public void Complete(ReadOnlyMemory<byte> request, TaskCompletionSource<ReadOnlyMemory<byte>> flight, ReadOnlyMemory<byte> response)
    {
        response = Set(request, response);
        Land(request, flight);
        flight.TrySetResult(response);
    }

    /// <summary>
    /// Nada se cachea: los que esperan reciben el mismo fallo y la siguiente llamada vuelve a intentarlo.
    /// Idempotente (no-op tras <see cref="Complete"/>); sin <paramref name="error"/>, el lider respondio sin resultado cacheable (rechazo).
    /// </summary>
    public void Fail(ReadOnlyMemory<byte> request, TaskCompletionSource<ReadOnlyMemory<byte>> flight, Exception? error)
    {
        if (flight.Task.IsCompleted) return;
        Land(request, flight);
        flight.TrySetException(error ?? new InvalidOperationException("The concurrent identical request didn't produce a cacheable response."));
    }

    // Solo el vuelo propio: tras Complete otro lider pudo empezar con la misma clave.
    private void Land(ReadOnlyMemory<byte> request, TaskCompletionSource<ReadOnlyMemory<byte>> flight)
    {
        if (_flights.TryGetValue(request.Span, out var key, out var current) && ReferenceEquals(current, flight))
            _flights.Dictionary.TryRemove(new KeyValuePair<ReadOnlyMemory<byte>, TaskCompletionSource<ReadOnlyMemory<byte>>>(key, flight));
    }

    public bool TryGet(ReadOnlyMemory<byte> request, out ReadOnlyMemory<byte> response)
    {
        if (_lookup.TryGetValue(request.Span, out var key, out var entry))
        {
            if (entry.Expires > Environment.TickCount64)
            {
                response = entry.Response;
                return true;
            }

            Remove(key, entry);
        }

        response = default;
        return false;
    }

    /// <returns>La copia propia de la respuesta (estable aunque el buffer original se reutilice).</returns>
    public ReadOnlyMemory<byte> Set(ReadOnlyMemory<byte> request, ReadOnlyMemory<byte> response)
    {
        var entry = new Entry(response.ToArray(), Environment.TickCount64 + _durationMs);

        if (_lookup.TryGetValue(request.Span, out var key, out var old))
            _entries.TryUpdate(key, entry, old);
        else if (Volatile.Read(ref _count) < _capacity || Purge() < _capacity)
            // La clave se copia en ByteContentComparer.Create: nunca se guarda memoria del pool.
            if (_lookup.TryAdd(request.Span, entry)) Interlocked.Increment(ref _count);

        return entry.Response;
    }

    private int Purge()
    {
        long now = Environment.TickCount64;

        foreach (var (key, entry) in _entries)
            if (entry.Expires <= now) Remove(key, entry);

        return Volatile.Read(ref _count);
    }

    // Solo si sigue siendo la misma entrada: un Set concurrente pudo refrescarla.
    private void Remove(ReadOnlyMemory<byte> key, Entry entry)
    {
        if (_entries.TryRemove(new KeyValuePair<ReadOnlyMemory<byte>, Entry>(key, entry))) Interlocked.Decrement(ref _count);
    }
}
