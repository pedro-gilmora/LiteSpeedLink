using System.Collections.Concurrent;
using System.Net;

namespace SourceCrafter.LiteSpeedLink;

/// <summary>
/// Conexiones simultaneas por direccion remota: sin el, quien abre <c>maxConnections</c> conexiones ociosas deja fuera al resto.
/// IPv4 cuenta por direccion; IPv6 por prefijo /64 (quien tiene un /64 controla 2^64 direcciones). Una consulta al aceptar, nada por llamada.
/// </summary>
public sealed class AddressLimiter(int limit)
{
    private readonly ConcurrentDictionary<IPAddress, int> _counts = new();

    /// <summary><c>false</c> si la direccion ya tiene <c>limit</c> conexiones; con <c>true</c>, <paramref name="key"/> se devuelve con <see cref="Exit"/>.</summary>
    public bool TryEnter(EndPoint? remote, out IPAddress? key)
    {
        // Sin direccion (UDS, transporte en memoria): no aplica.
        if (remote is not IPEndPoint { Address: var address })
        {
            key = null;
            return true;
        }

        key = Key(address);

        if (_counts.AddOrUpdate(key, 1, static (_, n) => n + 1) <= limit) return true;

        Exit(key);
        key = null;
        return false;
    }

    public void Exit(IPAddress? key)
    {
        // A 0 se quita la entrada: direcciones de un solo uso no acumulan memoria.
        if (key is not null && _counts.AddOrUpdate(key, 0, static (_, n) => n - 1) <= 0)
            _counts.TryRemove(new(key, 0));
    }

    private static IPAddress Key(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();

        if (address.AddressFamily is not System.Net.Sockets.AddressFamily.InterNetworkV6) return address;

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();

        return new IPAddress(bytes);
    }
}
