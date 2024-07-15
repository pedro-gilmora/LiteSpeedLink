using FluentAssertions;

using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;

using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;

using System.ComponentModel.Design;
using System.Diagnostics;
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

        using UdpClient server = Server.StartUdpServer<MockService>(serverPort, null!,
        new()
        {
            {
                0,
                static (context, token) =>
                {
                    var (a, b, isOdd) = context.Get<(int, int, bool)>();

                    return context.ReturnAsync(a + b, token);
                }
            },
            {
                1,
                static (context, token) =>
                {
                    string body = context.Get<string>()!;

                    return context.ReturnAsync(body.Reverse().ToArray(), token);
                }
            },
            {
                2,
                async static (context, token) =>
                {
                    for (int i = 1; i <= 10; i++)
                    {
                        await context.ReturnAsync(i, token).ConfigureAwait(false);
                    }

                    return await context.EndStreamingAsync(token);
                }
            } 
        }, default);

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

        await using var server = await Server.StartQuicServerAsync<MockService>(serverPort, null!, new()
        {
            {
                0,
                static (context, token) =>
                {
                    var (a, b, isOdd) = context.Read<(int, int, bool)>();

                    return context.ReturnAsync(a + b, token);
                }
            },
            {
                1,
                static (context, token) =>
                {
                    string body = context.Read<string>()!;

                    return context.ReturnAsync(body.Reverse().ToArray(), token);
                }
            },
            {
                2,
                async static (context, token) =>
                {
                    for (int i = 1; i <= 10; i++)
                    {
                        await context.YieldAsync(i, token).ConfigureAwait(false);
                    }

                    return await context.EndStreamingAsync(token);
                }
            }
        },
        cert, default);

        var timeStamp = Stopwatch.GetTimestamp();

        (int, int)[] dataSet = [(1, 5), (7, 4), (3, 6), (8, 0), (4, 9), (1, 1), (7, 4), (3, 6), (8, 0), (4, 9), (1, 5), (7, 4), (3, 6), (8, 0), (4, 9), (1, 5), (7, 4), (3, 6), (8, 0), (4, 9)];

        int i = 1;

        using var connection = new DnsEndPoint(serverIp, serverPort).AsQuicConnection(cert);

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

    [Fact]
    public async Task TestTcp()
    {
        const string serverIp = "localhost";
        const int serverPort = 5001;

        var cert = Constants.GetDevCert();

        using var server = Server.StartTcpServer<MockService>(serverPort, null!,
        new(){
            {
                0,
                static (context, token) =>
                {
                    var (a, b, isOdd) = context.Read<(int, int, bool)>();

                    return context.ReturnAsync(a + b, token);
                }
            },
            {
                1,
                static (context, token) =>
                {
                    string body = context.Read<string>()!;

                    return context.ReturnAsync(body.Reverse().ToArray(), token);
                }
            },
            {
                2,
                async static (context, token) =>
                {
                    for (int i = 1; i <= 10; i++)
                    {
                        await context.YieldAsync(i, token).ConfigureAwait(false);
                    }

                    return await context.EndStreamingAsync(token);
                }
            }
        }, cert, default);

        var timeStamp = Stopwatch.GetTimestamp();

        (int, int)[] dataSet = [(1, 5), (7, 4), (3, 6), (8, 0), (4, 9), (1, 1), (7, 4), (3, 6), (8, 0), (4, 9), (1, 5), (7, 4), (3, 6), (8, 0), (4, 9), (1, 5), (7, 4), (3, 6), (8, 0), (4, 9)];

        int i = 1;

        using var connection = new DnsEndPoint(serverIp, serverPort).AsTcpConnection(cert);

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