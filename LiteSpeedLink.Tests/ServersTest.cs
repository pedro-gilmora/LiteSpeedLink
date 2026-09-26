using FluentAssertions;
using MemoryPack;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using SharedMemory;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;

using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Threading.Tasks.Dataflow;
using Xunit;
using Xunit.Abstractions;

namespace LiteSpeedLink.Tests;

public class ServersTest(ITestOutputHelper output)
{
    [Fact]
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    public void TestMemory()
    {
        const int timeout = 1000;
        string MmfName = $"Test-{Guid.CreateVersion7()}";
        using (Server.StartMemoryServer(MmfName, HandleMemoryRequest, () => { }, timeout))
        {
            using var connection = new MemoryConnection(MmfName, timeout);

            (int, int)[] dataSet = [(1, 5), (7, 4), (3, 6), (8, 0), (4, 9), (1, 1), (7, 4), (3, 6), (8, 0), (4, 9), (1, 5), (7, 4), (3, 6), (8, 0), (4, 9), (1, 5), (7, 4), (3, 6), (8, 0), (4, 9)];

            int i = 1;

            foreach (var (a, b) in dataSet)
            {
                if (a > b)
                {
                    string payload = $"Hello from client {++i}";
                    var message = connection.Get<string, string>(1, payload);
                    var expected = new string([.. payload.Reverse()]);
                    message!.Equals(expected);
                }
                else if (a < b)
                {
                    var message = connection.Get<(int, int, bool), int>(0, (a, b, i % 2 == 0));
                    var expected = a + b;
                    message.Equals(expected);
                }
                else
                {
                    int y = 1;
                    foreach (var item in connection.Enumerate<int>(2))
                    {
                        item.Should().Be(y++);
                    }
                }
            }
        }
    }

    [Fact]
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    public async Task TestMemoryAsync()
    {
        const int timeout = 1000;
        string MmfName = $"Test-{Guid.CreateVersion7()}";
        using (Server.StartMemoryServerAsync(MmfName, HandleAsyncMemoryRequest, () => { }, timeout))
        {
            using var connection = new MemoryConnection(MmfName, timeout);

            (int, int)[] dataSet = [(1, 5), (7, 4), (3, 6), (8, 0), (4, 9), (1, 1), (7, 4), (3, 6), (8, 0), (4, 9), (1, 5), (7, 4), (3, 6), (8, 0), (4, 9), (1, 5), (7, 4), (3, 6), (8, 0), (4, 9)];

            int i = 1;

            foreach (var (a, b) in dataSet)
            {
                if (a > b)
                {
                    string payload = $"Hello from client {++i}";
                    var message = connection.Get<string, string>(1, payload);
                    var expected = new string([.. payload.Reverse()]);
                    message!.Equals(expected);
                }
                else if (a < b)
                {
                    var message = connection.Get<(int, int, bool), int>(0, (a, b, i % 2 == 0));
                    var expected = a + b;
                    message.Equals(expected);
                }
                else
                {
                    int y = 1;
                    foreach (var item in connection.Enumerate<int>(2))
                    {
                        item.Should().Be(y++);
                    }
                }
            }
        }
    }

    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    static async Task<byte[]> HandleAsyncMemoryRequest(long op, MemoryRequestContext ctx, CancellationToken token)
    {
        switch (op)
        {
            case 0:

                var (a, b, _) = ctx.Get<(int, int, bool)>();

                return ctx.Return(a + b);

            case 1:

                string body = ctx.Get<string>()!;

                return ctx.Return(new string([.. body.Reverse()]));

            case 2:

                return await ctx.Yield(StreamInts(token));

                static IAsyncEnumerable<int> StreamInts(CancellationToken token)
                {
                    BufferBlock<int> buffer = new();

                    for (int i = 1; i <= 10; i++)
                    {
                        buffer.Post(i);
                    }
                    buffer.Complete();
                    return buffer.ReceiveAllAsync(token);
                }

            default: return ctx.NotFound();
        }
    }

    [Fact]
    public async Task TestUdpConcurrentRoundtrips()
    {
        const int serverPort = 5003;

        using UdpClient server = Server.StartUdpServer(serverPort, HandleUdpRequestAsync, () => { }, default);
        using var connection = new DnsEndPoint("localhost", serverPort).AsUdpConnection();

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(i =>
            connection.GetAsync<(int, int, bool), int>(0, (i, i, false)).AsTask()));

        results.Should().Equal(Enumerable.Range(0, 200).Select(i => i * 2));
    }

    [Fact]
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    public async Task TestUdp()
    {
        const string serverIp = "localhost";
        const int serverPort = 5000;

        using UdpClient server = Server.StartUdpServer(serverPort, HandleUdpRequestAsync, () => { }, default);

        var timeStamp = Stopwatch.GetTimestamp();

        (int, int)[] dataSet = [(1, 5), (7, 4), (3, 6), (8, 0), (4, 9), (1, 1), (7, 4), (3, 6), (8, 0), (4, 9), (1, 5), (7, 4), (3, 6), (8, 0), (4, 9), (1, 5), (7, 4), (3, 6), (8, 0), (4, 9)];

        int i = 1;

        using var connection = new DnsEndPoint(serverIp, serverPort).AsUdpConnection();

        foreach (var (a, b) in dataSet)
        {
            if (a > b)
            {
                string payload = $"Hello from client {++i}";
                var message = await connection.GetAsync<string, string>(1, payload);

                message.Should().Be(new string([.. payload.Reverse()]));
            }
            else if (a < b)
            {
                var message = await connection.GetAsync<(int, int, bool), int>(0, (a, b, i % 2 == 0));

                message.Should().Be(a + b);
            }
            else
            {
                int y = 1;
                await foreach (var item in connection.EnumerateAsync<int>(2))
                {
                    item.Should().Be(y++);
                }
            }
        }

        output.WriteLine($"Took: {Stopwatch.GetElapsedTime(timeStamp)}");
    }

    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    [Fact]
    public async Task TestQuic()
    {
        const string serverIp = "localhost";
        const int serverPort = 5001;

        var cert = Constants.GetDevCert();

        await using var server = await Server.StartQuicServerAsync(serverPort, HandleRequestAsync, () => { }, cert, default);

        var timeStamp = Stopwatch.GetTimestamp();

        (int, int)[] dataSet = [(1, 5), (7, 4), (3, 6), (8, 0), (4, 9), (1, 1), (7, 4), (3, 6), (8, 0), (4, 9), (1, 5), (7, 4), (3, 6), (8, 0), (4, 9), (1, 5), (7, 4), (3, 6), (8, 0), (4, 9)];

        int i = 1;

        await using var connection = new DnsEndPoint(serverIp, serverPort).AsQuicConnection(cert);

        foreach (var (a, b) in dataSet)
        {
            if (a > b)
            {
                string payload = $"Hello from client {++i}";
                var message = await connection.GetAsync<string, string>(1, payload);

                message.Should().Be(new string([.. payload.Reverse()]));
            }
            else if (a < b)
            {
                var message = await connection.GetAsync<(int, int, bool), int>(0, (a, b, i % 2 == 0));

                message.Should().Be(a + b);
            }
            else
            {
                int y = 1;
                await foreach (var item in connection.EnumerateAsync<int>(2))
                {
                    item.Should().Be(y++);
                }
            }
        }

        output.WriteLine($"Took: {Stopwatch.GetElapsedTime(timeStamp)}");
    }

    private ValueTask<ResponseStatus> HandleRequestAsync(long id, RequestContext ctx, CancellationToken token)
    {
        switch (id)
        {
            case 0:
                var (a, b, isOdd) = ctx.Get<(int, int, bool)>();

                return ctx.ReturnAsync(a + b);
            case 1:
                string body = ctx.Get<string>()!;

                return ctx.ReturnAsync(body.Reverse().ToArray());
            case 2:

                return ctx.EnumerateAsync(SendItems);

                static IEnumerable<int> SendItems()
                {
                    for (int i = 1; i <= 10; i++)
                    {
                        yield return i;
                    }
                }
        }

        return ctx.NotFoundAsync();
    }

    private async ValueTask<ResponseStatus> HandleUdpRequestAsync(long id, UdpRequestContext ctx, CancellationToken token)
    {
        switch (id)
        {
            case 0:
                var (a, b, _) = ctx.Get<(int, int, bool)>();

                return await ctx.ReturnAsync(a + b);

            case 1:

                string body = ctx.Get<string>()!;

                return await ctx.ReturnAsync(body.Reverse().ToArray());

            case 2:

                for (int i = 1; i <= 10; i++)
                {
                    await ctx.YieldAsync(i);
                }

                return await ctx.EndStreamingAsync();
        }

        return await ctx.NotFoundAsync();
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void RPC_SlaveStreaming()
    {
        var ipcName = Guid.CreateVersion7().ToString();
        //RpcBuffer ipcMaster = null!;
        RpcBuffer ipcSlave = null!;

        ipcSlave = new RpcBuffer(ipcName, (msgId, payload) =>
        {
            for (int i = 1; i < 11; i++)
            {
                ipcSlave.RemoteRequest([(byte)i]);
            }

            ipcSlave.RemoteRequest(null);
        });

        int y = 0;

        foreach (var element in GetEnumerable())
        {
            element.Should().Be(++y);
        }

        ipcSlave.Dispose();
        IEnumerable<int> GetEnumerable()
        {
            BufferBlock<int> _buffer = new();

            using RpcBuffer rpc = new(ipcName, (id, payload) =>
            {
                if (payload?.Length > 0)
                {
                    _buffer.Post(payload[0]);
                }
                else
                {
                    _buffer.Complete();
                }
            });

            rpc.RemoteRequest();

            return _buffer.ReceiveAllAsync().ToBlockingEnumerable();
        }
    }

    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    private static byte[] HandleMemoryRequest(long op, MemoryRequestContext ctx, CancellationToken token)
    {
        switch (op)
        {
            case 0:

                var (a, b, _) = ctx.Get<(int, int, bool)>();

                return ctx.Return(a + b);

            case 1:

                string body = ctx.Get<string>()!;

                return ctx.Return(new string([.. body.Reverse()]));

            case 2:

                return ctx.Yield(StreamInts());

                static IEnumerable<int> StreamInts()
                {
                    for (int i = 1; i <= 10; i++)
                    {
                        yield return i;
                    }
                    yield break;
                }

            default: return ctx.NotFound();
        }
    }

    [Fact]
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    public async Task TestTcp()
    {
        const string serverIp = "localhost";
        const int serverPort = 5001;

        using var server = Server.StartTcpServer(serverPort, HandleRequestAsync, () => { }, null, default);

        var timeStamp = Stopwatch.GetTimestamp();

        (int, int)[] dataSet = [(1, 5), (7, 4), (3, 6), (8, 0), (4, 9), (1, 1), (7, 4), (3, 6), (8, 0), (4, 9), (1, 5), (7, 4), (3, 6), (8, 0), (4, 9), (1, 5), (7, 4), (3, 6), (8, 0), (4, 9)];

        int i = 1;

        await using var connection = new DnsEndPoint(serverIp, serverPort).AsTcpConnection();

        foreach (var (a, b) in dataSet)
        {
            if (a > b)
            {
                string payload = $"Hello from client {++i}";
                var message = await connection.GetAsync<string, string>(1, payload);

                message.Should().Be(new string([.. payload.Reverse()]));
            }
            else if (a < b)
            {
                var message = await connection.GetAsync<(int, int, bool), int>(0, (a, b, i % 2 == 0));

                message.Should().Be(a + b);
            }
            else
            {
                int y = 1;
                await foreach (var item in connection.EnumerateAsync<int>(2))
                {
                    item.Should().Be(y++);
                }
            }
        }

        output.WriteLine($"Took: {Stopwatch.GetElapsedTime(timeStamp)}");
    }

    [Fact]
    public async Task TestTcpRejectsOversizedFrame()
    {
        const int serverPort = 5004;

        using var server = Server.StartTcpServer(serverPort, HandleRequestAsync, () => { }, null, default);
        using var socket = new System.Net.Sockets.TcpClient();
        await socket.ConnectAsync("localhost", serverPort);
        var stream = socket.GetStream();

        await stream.WriteAsync(BitConverter.GetBytes(int.MaxValue));

        var read = await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        read.Should().Be(0);
    }

    [Fact]
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    public async Task TestTcpConcurrentRoundtrips()
    {
        const int serverPort = 5002;

        using var server = Server.StartTcpServer(serverPort, HandleRequestAsync, () => { }, null, default);

        await using var connection = new DnsEndPoint("localhost", serverPort).AsTcpConnection();

        var calls = Enumerable.Range(0, 500).Select(async n =>
        {
            switch (n % 4)
            {
                case 0:
                    (await connection.GetAsync<(int, int, bool), int>(0, (n, 7, false))).Should().Be(n + 7);
                    break;

                case 1:
                    var payload = $"payload-{n}";
                    (await connection.GetAsync<string, string>(1, payload)).Should().Be(new string([.. payload.Reverse()]));
                    break;

                case 2:
                    int y = 1;
                    await foreach (var item in connection.EnumerateAsync<int>(2)) item.Should().Be(y++);
                    y.Should().Be(11);
                    break;

                default:
                    var act = async () => await connection.GetAsync<int>(99);
                    await act.Should().ThrowAsync<NotImplementedException>();
                    break;
            }
        });

        await Task.WhenAll(calls);
    }
}

internal class MockService : IServiceProvider, IDisposable, IAsyncDisposable
{
    public void Dispose()
    {
        throw new NotImplementedException();
    }

    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    public object? GetService(Type serviceType)
    {
        throw new NotImplementedException();
    }
}