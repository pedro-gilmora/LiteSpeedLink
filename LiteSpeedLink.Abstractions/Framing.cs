using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SourceCrafter.LiteSpeedLink;

/// <summary>
/// Lectura y escritura de la cabecera de operación, con endianness explícita y sin asignaciones.
/// </summary>
public static class Framing
{
    public const int OpIdSize = sizeof(long);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteOpId(Span<byte> destination, long op) =>
        BinaryPrimitives.WriteInt64LittleEndian(destination, op);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadOpId(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadInt64LittleEndian(source);

    /// <summary>
    /// Lee la cabecera desde una secuencia, contemplando que los 8 bytes puedan quedar
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

    /// <summary>Tamano del prefijo de longitud que delimita cada mensaje.</summary>
    public const int LengthPrefixSize = sizeof(int);

    // ponytail: tope fijo; si un contrato necesita mensajes mayores, hacerlo configurable por host.
    /// <summary>Tamano maximo aceptado para un mensaje; por encima se corta la conexion.</summary>
    public const int MaxFrameSize = 16 * 1024 * 1024;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteLength(Span<byte> destination, int length) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination, length);

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

        if (buffer.Length < LengthPrefixSize) return false;

        Span<byte> lengthBytes = stackalloc byte[LengthPrefixSize];
        buffer.Slice(0, LengthPrefixSize).CopyTo(lengthBytes);

        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);

        if (length is < 0 or > MaxFrameSize) throw new InvalidDataException($"Invalid frame length: {length}.");

        if (buffer.Length < LengthPrefixSize + length) return false;

        frame = buffer.Slice(LengthPrefixSize, length);
        buffer = buffer.Slice(LengthPrefixSize + length);

        return true;
    }

    /// <summary>Identificador que empareja cada respuesta con su peticion en una conexion multiplexada.</summary>
    public const int CorrelationIdSize = sizeof(int);

    /// <summary>Cabecera de trama multiplexada: <c>[len:int32][corrId:int32]</c>.</summary>
    public const int FrameHeaderSize = LengthPrefixSize + CorrelationIdSize;

    /// <summary>Tamano del estado que encabeza toda respuesta.</summary>
    public const int StatusSize = sizeof(byte);

    /// <summary>
    /// Escribe <c>[len][corrId]</c>, donde <c>len</c> cuenta el correlation id mas el contenido.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteFrameHeader(Span<byte> destination, int correlationId, int contentLength)
    {
        WriteLength(destination, CorrelationIdSize + contentLength);
        BinaryPrimitives.WriteInt32LittleEndian(destination[LengthPrefixSize..], correlationId);
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
