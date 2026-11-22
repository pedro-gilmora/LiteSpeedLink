namespace SourceCrafter.LiteSpeedLink;

/// <summary>
/// Bytes de cuerpos en curso de una conexion (como la ventana de conexion de HTTP/2): al pasar del presupuesto se deja de leer la conexion
/// (back-pressure TCP) hasta que terminen peticiones. Con la conexion vacia se admite siempre la siguiente, mayor o no: sin interbloqueo
/// con mensajes mayores que el presupuesto. Solo el lector de la conexion reserva; la liberacion llega de los handlers.
/// </summary>
internal sealed class ByteBudget(long limit)
{
    private long _used;
    private int _waiting;
    private SemaphoreSlim? _released;

    /// <summary>Camino rapido sin esperar ni asignar: dos operaciones atomicas por peticion (esta y <see cref="Release"/>).</summary>
    public ValueTask EnterAsync(int bytes, CancellationToken token)
    {
        if (TryEnter(bytes)) return default;

        return WaitAsync(bytes, token);
    }

    public void Release(int bytes)
    {
        Interlocked.Add(ref _used, -bytes);

        if (Volatile.Read(ref _waiting) == 1 && Interlocked.Exchange(ref _waiting, 0) == 1) _released!.Release();
    }

    private bool TryEnter(int bytes)
    {
        // Un solo lector reserva: entre leer y sumar, las liberaciones solo bajan el uso.
        long used = Volatile.Read(ref _used);
        if (used != 0 && used + bytes > limit) return false;

        Interlocked.Add(ref _used, bytes);
        return true;
    }

    private async ValueTask WaitAsync(int bytes, CancellationToken token)
    {
        _released ??= new SemaphoreSlim(0);

        while (true)
        {
            Volatile.Write(ref _waiting, 1);

            // Tras marcar la espera: una liberacion anterior ya se ve aqui; una posterior soltara el semaforo.
            if (TryEnter(bytes))
            {
                Volatile.Write(ref _waiting, 0);
                return;
            }

            await _released.WaitAsync(token).ConfigureAwait(false);
        }
    }

    public void Dispose() => _released?.Dispose();
}
