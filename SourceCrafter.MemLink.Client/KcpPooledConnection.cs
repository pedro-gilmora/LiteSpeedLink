using KcpTransport;

using MemoryPack;

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace SourceCrafter.MemLink;

public sealed class KcpPooledConnection(string key, KcpConnection connection, DateTime lastTimeCheck, bool disconnected = false) : IAsyncDisposable
{

    private KcpStream stream = null!;

    readonly SemaphoreSlim readWriteLock = new(1);

    internal CancellationTokenSource CancellationSource = new();

    internal DateTime LastTimeCheck = lastTimeCheck;

    public volatile bool Disconnected = disconnected;

    internal string Key = key;

    public KcpConnection Connection = connection;

    internal async ValueTask InitAsync()
    {
        stream ??= await Connection.OpenOutboundStreamAsync();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<T> ReceiveAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>()
    {
        try
        {
            await readWriteLock.WaitAsync();

            var lenHeader = new byte[4];

            await stream.ReadExactlyAsync(lenHeader);

            var resBytes = new byte[BitConverter.ToInt32(lenHeader)];

            await stream.ReadExactlyAsync(resBytes);

            //lastReadPos += resBytes.Length;

            return MemoryPackSerializer.Deserialize<T>(resBytes)!;
        }
        catch
        {
            throw;
        }
        finally
        {
            readWriteLock.Release();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(T payload)
    {
        try
        {
            await readWriteLock.WaitAsync();

            Memory<byte> itemBytes = MemoryPackSerializer.Serialize(payload);

            var datagram = new byte[4 + itemBytes.Length];

            BitConverter.GetBytes(itemBytes.Length).CopyTo(datagram, 0);

            itemBytes.Span.CopyTo(datagram);

            //lastWritenPos += datagram.Length;

            await stream.WriteAsync(datagram);
        }
        catch
        {
            throw;
        }
        finally
        {
            readWriteLock.Release();

        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            readWriteLock.Dispose();

            await stream.DisposeAsync();

            CancellationSource.Cancel();
        }
        finally
        {
            if (!Disconnected)
            {
                Disconnected = true;
                Connection?.Dispose();
            }
        }
    }
}