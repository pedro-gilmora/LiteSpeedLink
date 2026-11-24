using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SourceCrafter.LiteSpeedLink;

/// <summary>
/// Lectura y escritura de la cabecera de operación, con endianness explícita y sin asignaciones.
/// </summary>
public static class Framing
{
    /// <summary>opId en el cable: 4 bytes (FNV-1a de 32 bits, colisiones rechazadas en compilacion); la API lo sigue tratando como long.</summary>
    public const int OpIdSize = sizeof(int);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteOpId(Span<byte> destination, long op) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination, (int)op);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadOpId(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadInt32LittleEndian(source);

    /// <summary>
    /// Lee la cabecera desde una secuencia, contemplando que sus bytes puedan quedar
    /// repartidos entre varios segmentos.
    /// </summary>
    public static long ReadOpId(in ReadOnlySequence<byte> source)
    {
        if (source.FirstSpan.Length >= OpIdSize)
            return ReadOpId(source.FirstSpan);

        Span<byte> header = stackalloc byte[OpIdSize];
        source.Slice(0, OpIdSize).CopyTo(header);

        return ReadOpId(header);
    }

    /// <summary>
    /// Espacio que se reserva para la longitud antes de conocerla. En el cable ocupa 1 byte (&lt; 128), 2 (&lt; 16 KB) o 4 (LEB128;
    /// los 4 bytes admiten la forma rellenada, que se usa cuando el escritor no pudo acortarla).
    /// </summary>
    public const int LengthPrefixSize = 4;

    /// <summary>Por debajo de este tamano el escritor acorta la longitud (desplazando el contenido como mucho 16 KB).</summary>
    private const int CompactLengthLimit = 1 << 14;

    /// <summary>Bytes que ocupa <paramref name="length"/> en el cable: 1, 2 o, desde 16 KB, la forma de 4 bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LengthSize(int length) => length < 0x80 ? 1 : length < CompactLengthLimit ? 2 : LengthPrefixSize;

    /// <summary>Escribe <paramref name="length"/> en <paramref name="size"/> bytes LEB128 (4 = forma rellenada si hace falta); devuelve <paramref name="size"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteLength(Span<byte> destination, int length, int size = LengthPrefixSize)
    {
        uint value = (uint)length;

        for (int i = 0; i < size - 1; i++, value >>= 7)
            destination[i] = (byte)(value | 0x80);

        destination[size - 1] = (byte)(value & 0x7F);

        return size;
    }

    // ponytail: tope fijo; si un contrato necesita mensajes mayores, hacerlo configurable por host.
    /// <summary>Tamano maximo aceptado para un mensaje; por encima se corta la conexion.</summary>
    public const int MaxFrameSize = 16 * 1024 * 1024;

    /// <summary>
    /// Cuerpo maximo de una peticion por defecto (4 MB, como gRPC); <c>[MaxMessageSize]</c> lo cambia por operacion en compilacion,
    /// hasta lo que cabe en <see cref="MaxFrameSize"/>.
    /// </summary>
    public const int DefaultMaxMessageSize = 4 * 1024 * 1024;

    /// <summary>Cuerpo maximo que admite una trama multiplexada: <see cref="MaxFrameSize"/> menos corrId y opId.</summary>
    public const int MaxMessageSize = MaxFrameSize - CorrelationIdSize - OpIdSize;

    /// <summary>
    /// Longitud anunciada al principio de <paramref name="buffer"/>, sin consumirla. <c>false</c> si aun no llegaron sus bytes.
    /// Una longitud de mas de 4 bytes o mayor que <see cref="MaxFrameSize"/> es invalida.
    /// </summary>
    public static bool TryReadLength(in ReadOnlySequence<byte> buffer, out int length, out int prefixSize)
    {
        var first = buffer.FirstSpan;
        uint value = 0;

        for (int i = 0; i < LengthPrefixSize; i++)
        {
            if (i >= buffer.Length)
            {
                (length, prefixSize) = (0, 0);
                return false;
            }

            uint b = i < first.Length ? first[i] : buffer.Slice(i, 1).FirstSpan[0];
            value |= (b & 0x7F) << (7 * i);

            if ((b & 0x80) == 0)
            {
                if (value > MaxFrameSize) throw new InvalidDataException($"Invalid frame length: {value}.");
                (length, prefixSize) = ((int)value, i + 1);
                return true;
            }
        }

        throw new InvalidDataException("Invalid frame length prefix.");
    }

    /// <summary>
    /// Intenta extraer un mensaje completo del buffer. TCP es un flujo: una lectura no equivale
    /// a un mensaje, puede entregar fragmentos o varios mensajes juntos.
    /// </summary>
    /// <param name="buffer">Al volver, avanza mas alla del mensaje consumido.</param>
    /// <param name="frame">Contenido del mensaje, sin el prefijo de longitud.</param>
    /// <returns><c>true</c> si habia un mensaje completo disponible.</returns>
    public static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame)
    {
        frame = default;

        if (!TryReadLength(buffer, out int length, out int prefix) || buffer.Length < prefix + length) return false;

        frame = buffer.Slice(prefix, length);
        buffer = buffer.Slice(prefix + length);

        return true;
    }

    /// <summary>Identificador que empareja cada respuesta con su peticion en una conexion multiplexada.</summary>
    public const int CorrelationIdSize = sizeof(int);

    /// <summary>Cabecera de trama multiplexada reservada al escribir: <c>[len (hasta 4)][corrId:int32]</c>.</summary>
    public const int FrameHeaderSize = LengthPrefixSize + CorrelationIdSize;

    /// <summary>Tamano del estado que encabeza toda respuesta.</summary>
    public const int StatusSize = sizeof(byte);

    /// <summary>
    /// Escribe <c>[len][corrId]</c> con la longitud minima (contenido conocido de antemano: tramas de control); devuelve los bytes escritos.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteFrameHeader(Span<byte> destination, int correlationId, int contentLength)
    {
        int prefix = WriteLength(destination, CorrelationIdSize + contentLength, LengthSize(CorrelationIdSize + contentLength));
        BinaryPrimitives.WriteInt32LittleEndian(destination[prefix..], correlationId);
        return prefix + CorrelationIdSize;
    }

    /// <summary>
    /// Cierra una trama escrita en memoria contigua con <see cref="LengthPrefixSize"/> bytes reservados para la longitud
    /// (<paramref name="frame"/> = reserva + contenido): escribe la longitud y, si es corta, desplaza el contenido hacia la
    /// reserva. Devuelve los bytes que sobran al final (0 si la trama queda con la forma de 4 bytes).
    /// </summary>
    public static int CompleteFrame(Span<byte> frame)
    {
        int length = frame.Length - LengthPrefixSize;
        int size = LengthSize(length);

        if (size < LengthPrefixSize) frame[LengthPrefixSize..].CopyTo(frame[size..]);

        WriteLength(frame, length, size);

        return LengthPrefixSize - size;
    }

    /// <summary>Extrae una trama multiplexada completa: su correlation id y su contenido.</summary>
    public static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out int correlationId, out ReadOnlySequence<byte> content)
    {
        correlationId = 0;
        content = default;

        if (!TryReadFrame(ref buffer, out var frame)) return false;

        if (frame.Length < CorrelationIdSize) throw new InvalidDataException($"Frame too short: {frame.Length}.");

        if (frame.FirstSpan.Length >= CorrelationIdSize)
        {
            correlationId = BinaryPrimitives.ReadInt32LittleEndian(frame.FirstSpan);
        }
        else
        {
            Span<byte> id = stackalloc byte[CorrelationIdSize];
            frame.Slice(0, CorrelationIdSize).CopyTo(id);
            correlationId = BinaryPrimitives.ReadInt32LittleEndian(id);
        }

        content = frame.Slice(CorrelationIdSize);
        return true;
    }

    /// <summary>Lee el estado que encabeza una respuesta.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ResponseStatus ReadStatus(in ReadOnlySequence<byte> response) =>
        response.IsEmpty
            ? throw new InvalidDataException("Empty response.")
            : (ResponseStatus)response.FirstSpan[0];
}
