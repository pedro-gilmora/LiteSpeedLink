using System.Buffers;

namespace SourceCrafter.LiteSpeedLink;

/// <summary>
/// Escribe tramas <c>[len][corrId][cabecera extra][contenido]</c> directamente sobre el
/// <see cref="IBufferWriter{T}"/> de destino, sin buffers intermedios: la cabecera se reserva al
/// empezar y la longitud se completa al terminar, contando lo que el serializador escribio.
/// </summary>
/// <remarks>
/// La cabecera se rellena despues de <c>Advance</c>. Es valido para <c>PipeWriter</c> porque nada
/// se envia hasta el <c>FlushAsync</c>, que debe llamarse siempre despues de <see cref="EndFrame"/>.
/// No es seguro para uso concurrente: quien lo usa serializa el acceso.
/// </remarks>
public sealed class FrameWriter(IBufferWriter<byte> inner, bool correlated = true) : IBufferWriter<byte>
{
    private readonly int _headerSize = correlated ? Framing.FrameHeaderSize : Framing.LengthPrefixSize;
    private Memory<byte> _header;
    private int _correlationId, _extra, _written;

    /// <summary>Reserva la cabecera y devuelve el hueco para la cabecera extra (opId o estado).</summary>
    public Span<byte> BeginFrame(int correlationId, int extraHeaderSize)
    {
        _correlationId = correlationId;
        _extra = extraHeaderSize;
        _written = 0;

        int size = _headerSize + extraHeaderSize;

        _header = inner.GetMemory(size)[..size];
        inner.Advance(size);

        return _header.Span[_headerSize..];
    }

    public void EndFrame()
    {
        if (correlated)
            Framing.WriteFrameHeader(_header.Span, _correlationId, _extra + _written);
        else
            Framing.WriteLength(_header.Span, _extra + _written);

        _header = default;
    }

    public void Advance(int count)
    {
        inner.Advance(count);
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

    public Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
}
