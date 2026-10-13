using System.Net;
using System.Runtime.CompilerServices;
using FluentAssertions;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;
using Xunit;

namespace LiteSpeedLink.Tests;

public class StreamCancellationTest
{
    /// <summary>Salir del <c>await foreach</c> avisa al host: el productor infinito se cancela y la conexion sigue sana.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AbandonedStreamStopsHostProducer(bool uds, bool batched)
    {
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<int> Produce([EnumeratorCancellation] CancellationToken token = default)
        {
            try
            {
                for (int i = 0; ; i++)
                {
                    await Task.Delay(1, token);
                    yield return i;
                }
            }
            finally
            {
                stopped.TrySetResult(token.IsCancellationRequested);
            }
        }

        RequestHandler handler = (op, ctx, _) => op == 0
            ? ctx.ReturnAsync(ctx.Get<int>() * 2)
            : batched ? ctx.EnumerateAsync(Produce()) : ctx.EnumerateAsync<int, Unbatched>(Produce());

        var path = Path.Combine(Path.GetTempPath(), $"lsl-{Guid.NewGuid():N}.sock");
        IDisposable? udsServer = null;
        System.Net.Sockets.TcpListener? tcpServer = null;

        if (uds) udsServer = Server.StartUdsServer(path, handler, () => { });
        else tcpServer = Server.StartTcpServer(0, handler, () => { });

        try
        {
            await using StreamConnection client = uds
                ? new UdsConnection(path)
                : new TcpConnection(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)tcpServer!.LocalEndpoint).Port));

            using var cts = new CancellationTokenSource(5000);
            int seen = 0;
            await foreach (var _ in client.EnumerateAsync<int>(1, cts.Token))
                if (++seen == 3) break;

            (await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3))).Should().BeTrue();
            (await client.GetAsync<int, int>(0, 21, cts.Token)).Should().Be(42);
        }
        finally
        {
            udsServer?.Dispose();
            tcpServer?.Stop();
        }
    }
}
