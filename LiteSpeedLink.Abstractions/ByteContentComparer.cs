namespace SourceCrafter.LiteSpeedLink;

/// <summary>
/// Igualdad por contenido para claves <see cref="ReadOnlyMemory{T}"/> (la suya compara referencia, offset y longitud).
/// Hash con <see cref="HashCode.AddBytes"/> (semilla aleatoria por proceso: resiste colisiones provocadas),
/// igualdad vectorizada con SequenceEqual; la busqueda alternativa por span evita copiar la clave al consultar.
/// Compartido por [ServerCache] (bytes de la peticion) y [ClientCache] (clave por bytes, SCLSL018).
/// </summary>
// ponytail: hashea la clave entera en cada consulta; XxHash3 (System.IO.Hashing) si un benchmark lo pide con claves > ~1 KB.
public sealed class ByteContentComparer :
    IEqualityComparer<ReadOnlyMemory<byte>>,
    IAlternateEqualityComparer<ReadOnlySpan<byte>, ReadOnlyMemory<byte>>
{
    public static readonly ByteContentComparer Instance = new();

    private ByteContentComparer() { }

    public bool Equals(ReadOnlyMemory<byte> x, ReadOnlyMemory<byte> y) => x.Span.SequenceEqual(y.Span);

    public int GetHashCode(ReadOnlyMemory<byte> obj) => GetHashCode(obj.Span);

    public bool Equals(ReadOnlySpan<byte> alternate, ReadOnlyMemory<byte> other) => alternate.SequenceEqual(other.Span);

    public int GetHashCode(ReadOnlySpan<byte> alternate)
    {
        HashCode hash = new();
        hash.AddBytes(alternate);
        return hash.ToHashCode();
    }

    public ReadOnlyMemory<byte> Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();
}
