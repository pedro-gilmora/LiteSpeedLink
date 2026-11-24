using SourceCrafter.LiteSpeedLink;

namespace LiteSpeedLink.Tests;

/// <summary>Tramas exactas del cable, para los tests que hablan con el host o el cliente a mano: <c>[len][corrId][...]</c>, longitud minima.</summary>
internal static class TestFrames
{
    /// <summary>Bytes de <c>[len][corrId]</c> para un contenido de <paramref name="contentLength"/> bytes.</summary>
    public static int HeaderSize(int contentLength) => Framing.LengthSize(Framing.CorrelationIdSize + contentLength) + Framing.CorrelationIdSize;

    /// <summary><c>[len][corrId][contenido]</c>.</summary>
    public static byte[] Frame(int corrId, ReadOnlySpan<byte> content)
    {
        var frame = new byte[HeaderSize(content.Length) + content.Length];
        content.CopyTo(frame.AsSpan(Framing.WriteFrameHeader(frame, corrId, content.Length)));
        return frame;
    }

    /// <summary>Peticion <c>[len][corrId][opId][cuerpo]</c>.</summary>
    public static byte[] Request(int corrId, long op, ReadOnlySpan<byte> body)
    {
        var content = new byte[Framing.OpIdSize + body.Length];
        Framing.WriteOpId(content, op);
        body.CopyTo(content.AsSpan(Framing.OpIdSize));
        return Frame(corrId, content);
    }

    /// <summary>Respuesta <c>[len][corrId][estado][cuerpo]</c>.</summary>
    public static byte[] Response(int corrId, ResponseStatus status, ReadOnlySpan<byte> body)
    {
        var content = new byte[Framing.StatusSize + body.Length];
        content[0] = (byte)status;
        body.CopyTo(content.AsSpan(Framing.StatusSize));
        return Frame(corrId, content);
    }
}
