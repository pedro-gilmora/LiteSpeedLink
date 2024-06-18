using KcpTransport;

using MemoryPack;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace SourceCrafter.MemLink;

public static class ServiceClient
{
    public static async ValueTask<(KcpConnection, KcpStream)> ConnectAsync(string host, int port, KcpClientConnectionOptions? opts = null)
    {
        string key = ($"{host}:{port}");

        opts ??= new() { RemoteEndPoint = IPEndPoint.Parse(key) };

        var connection = await KcpConnection.ConnectAsync(host, port);

        var stream = await connection.OpenOutboundStreamAsync();

        return (connection, stream);
    }

    public static async ValueTask<ResponseData> GetAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TRequest>(this KcpStream stream, string opCode, TRequest payload)
    {
        #region Send Message

        Memory<byte> bytes = MemoryPackSerializer.Serialize((0, opCode, payload));

        BitConverter.GetBytes(bytes.Length - 44).CopyTo(bytes);

        await stream.WriteAsync(bytes);

        #endregion

        #region Receive Message	

        await stream.ReadExactlyAsync(bytes = new byte[8]);

        await stream.ReadExactlyAsync(bytes = new byte[BitConverter.ToInt32(bytes[4..8].Span)]);

        #endregion

        return new(bytes);
    }
}
public struct ResponseData(Memory<byte> data)
{
    public T Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>()
        => MemoryPackSerializer.Deserialize<T>(data.Span)!;
}