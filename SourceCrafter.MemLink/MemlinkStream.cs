using KcpTransport;

using MemoryPack;

using System.Diagnostics.CodeAnalysis;

namespace SourceCrafter.MemLink;

public sealed class MemlinkStream(KcpStream stream) : IAsyncDisposable
{

    internal async ValueTask<T> ReadAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>()
    {
        var lenHeader = new byte[4];

        await stream.ReadExactlyAsync(lenHeader);

        var resBytes = new byte[BitConverter.ToInt32(lenHeader)];

        await stream.ReadExactlyAsync(resBytes);

        return MemoryPackSerializer.Deserialize<T>(resBytes)!;
    }

    internal ValueTask WriteAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(T item)
    {
        var itemBytes = MemoryPackSerializer.Serialize(item);

        var datagram = new byte[4 + itemBytes.Length];

        BitConverter.GetBytes(itemBytes.Length).CopyTo(datagram, 0);

        itemBytes.CopyTo(datagram, 4);

        return stream.WriteAsync(datagram);
    }

    public ValueTask DisposeAsync()
    {
        return stream.DisposeAsync();
    }

}