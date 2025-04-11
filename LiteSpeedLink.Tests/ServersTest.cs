using FluentAssertions;

using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;

using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;

using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;

using Xunit;
using Xunit.Abstractions;

namespace SourceCrafter.Communication.LiteSpeedLink.Tests;

public class ServersTest(ITestOutputHelper output)
{
    [Fact]
    public async Task TestUdp()
    {
        const string serverIp = "localhost";
        const int serverPort = 5000;

        using UdpClient server = Server.StartUdpServer(serverPort, HandleUdpRequestAsync, () => { } , default);

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

                message.Should().Be(new string(payload.Reverse().ToArray()));
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
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
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

                message.Should().Be(new string(payload.Reverse().ToArray()));
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

    private ValueTask<FlushResult> HandleRequestAsync(long id, RequestContext ctx, CancellationToken token)
    {
        switch (id)
        {
            case 0:
                var (a, b, isOdd) = ctx.Get<(int, int, bool)>();

                return ctx.ReturnAsync(a + b, token);
            case 1:
                string body = ctx.Get<string>()!;

                return ctx.ReturnAsync(body.Reverse().ToArray(), token);
            case 2:

                return ctx.EnumerateAsync(SendItems, token);

                static IEnumerable<int> SendItems()
                {
                    for (int i = 1; i <= 10; i++)
                    {
                        yield return i;
                    }
                }
        }

        return default;
    }

    private async ValueTask<int> HandleUdpRequestAsync(long id, UdpRequestContext ctx, CancellationToken token)
    {
        switch (id)
        {
            case 0:
            
                var (a, b, isOdd) = ctx.Get<(int, int, bool)>();

                return await ctx.ReturnAsync(a + b, token);
            
            case 1:

                string body = ctx.Get<string>()!;

                return await ctx.ReturnAsync(body.Reverse().ToArray(), token);
           
            case 2:

                for (int i = 1; i <= 10; i++)
                {
                    await ctx.ReturnAsync(i, token);
                }

                return await ctx.EndStreamingAsync(token);
        }

        return default;
    }

    [Fact]
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

                message.Should().Be(new string(payload.Reverse().ToArray()));
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