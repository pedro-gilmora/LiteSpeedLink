using System.Buffers;
using System.Buffers.Binary;

namespace SourceCrafter.LiteSpeedLink;

/// <summary>
/// Escribe tramas <c>[len][corrId][cabecera extra][contenido]</c> directamente sobre el
/// <see cref="IBufferWriter{T}"/> de destino, sin buffers intermedios.
/// </summary>
/// <remarks>
/// La longitud ocupa 1, 2 o 4 bytes (<see cref="Framing.LengthSize"/>) y no se conoce hasta terminar. La trama se escribe sobre el hueco
/// libre que da el destino sin confirmarla: al cerrar se escribe la longitud corta, el contenido se desplaza hacia ella y solo entonces se
/// confirma. Si el serializador necesita mas de lo que cabe en ese hueco, se confirma lo escrito y la longitud queda con la forma de 4 bytes
/// (se rellena al cerrar; valido para <c>PipeWriter</c> porque nada sale hasta el <c>FlushAsync</c>, que debe ir despues de
/// <see cref="EndFrame"/>). No es seguro para uso concurrente: quien lo usa serializa el acceso.
/// </remarks>
public sealed class FrameWriter(IBufferWriter<byte> inner, bool correlated = true) : IBufferWriter<byte>
{
    private readonly int _headerSize = correlated ? Framing.FrameHeaderSize : Framing.LengthPrefixSize;
    private Memory<byte> _frame;
    private int _position, _correlationId, _extra, _written;

    /// <summary>Reserva la cabecera y devuelve el hueco para la cabecera extra (opId o estado).</summary>
    public Span<byte> BeginFrame(int correlationId, int extraHeaderSize)
    {
        _correlationId = correlationId;
        _extra = extraHeaderSize;
        _written = 0;
        _position = _headerSize + extraHeaderSize;
        _frame = inner.GetMemory(_position);

        return _frame.Span[_headerSize.._position];
    }

    public void EndFrame()
    {
        if (correlated) BinaryPrimitives.WriteInt32LittleEndian(_frame.Span[Framing.LengthPrefixSize..], _correlationId);

        if (_position >= 0)
        {
            int trimmed = Framing.CompleteFrame(_frame.Span[.._position]);
            inner.Advance(_position - trimmed);
        }
        else
        {
            int content = (correlated ? Framing.CorrelationIdSize : 0) + _extra + _written;
            Framing.WriteLength(_frame.Span, content);
        }

        _frame = default;
    }

    public void Advance(int count)
    {
        if (_position >= 0) _position += count;
        else inner.Advance(count);

        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0) => Fits(sizeHint) ? _frame[_position..] : Spill().GetMemory(sizeHint);

    public Span<byte> GetSpan(int sizeHint = 0) => Fits(sizeHint) ? _frame.Span[_position..] : Spill().GetSpan(sizeHint);

    private bool Fits(int sizeHint) => _position >= 0 && _frame.Length - _position >= Math.Max(sizeHint, 1);

    /// <summary>No cabe en el hueco: se confirma lo escrito y el resto va directo al destino; la longitud se rellena en 4 bytes al cerrar.</summary>
    private IBufferWriter<byte> Spill()
    {
        if (_position >= 0)
        {
            inner.Advance(_position);
            _position = -1;
        }

        return inner;
    }
}
