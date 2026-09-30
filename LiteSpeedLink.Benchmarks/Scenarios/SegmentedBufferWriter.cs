using System.Buffers;

namespace SourceCrafter.LiteSpeedLink;

/// <summary>
/// <see cref="IBufferWriter{T}"/> sobre bloques alquilados al <see cref="ArrayPool{T}.Shared"/>.
/// Crecer no copia lo ya escrito: se encadena otro bloque. El resultado se expone como
/// <see cref="ReadOnlySequence{T}"/>, que MemoryPack lee sin aplanar.
/// </summary>
/// <remarks>No es seguro para uso concurrente. <see cref="Reset"/> devuelve los bloques al pool.</remarks>
public sealed class SegmentedBufferWriter(int blockSize = 4096) : IBufferWriter<byte>, IDisposable
{
    private Segment? _first, _last;
    private long _written;

    public long WrittenCount => _written;

    public ReadOnlySequence<byte> WrittenSequence =>
        _first is null ? default
        : ReferenceEquals(_first, _last) ? new(_first.Array, 0, _first.Used)
        : new(_first, 0, _last!, _last!.Used);

    public void Advance(int count)
    {
        _last!.Used += count;
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0) => Ensure(sizeHint).AsMemory(_last!.Used);

    public Span<byte> GetSpan(int sizeHint = 0) => Ensure(sizeHint).AsSpan(_last!.Used);

    private byte[] Ensure(int sizeHint)
    {
        if (sizeHint < 1) sizeHint = 1;

        if (_last is null || _last.Array.Length - _last.Used < sizeHint)
        {
            var seg = new Segment(ArrayPool<byte>.Shared.Rent(Math.Max(sizeHint, blockSize)), _written);
            if (_last is null) _first = seg; else _last.SetNext(seg);
            _last = seg;
        }

        return _last.Array;
    }

    public void Reset()
    {
        for (var s = _first; s is not null; s = (Segment?)s.Next)
            ArrayPool<byte>.Shared.Return(s.Array);

        _first = _last = null;
        _written = 0;
    }

    public void Dispose() => Reset();

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public readonly byte[] Array;
        public int Used;

        public Segment(byte[] array, long runningIndex)
        {
            Array = array;
            RunningIndex = runningIndex;
            Memory = array; // el ultimo se acota con endIndex = Used
        }

        public void SetNext(Segment next)
        {
            Memory = Array.AsMemory(0, Used);
            Next = next;
        }
    }
}
