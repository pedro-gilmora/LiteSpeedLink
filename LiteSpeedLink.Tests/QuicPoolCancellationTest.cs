using FluentAssertions;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;
using System.Net;
using System.Runtime.Versioning;
using Xunit;

namespace LiteSpeedLink.Tests;

/// <summary>Pool QUIC: cancelar una unaria a mitad no rompe el stream compartido ni a sus vecinas.</summary>
public class QuicPoolCancellationTest
{
    [Fact]
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    public async Task CancelledUnaryLeavesPoolUsable()
    {
        const int serverPort = 5030;
        var cert = Constants.GetDevCert();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await Server.StartQuicServerAsync(serverPort, async (op, ctx, _) =>
        {
            if (op == 1) await release.Task;
            return await ctx.ReturnAsync((int)op);
        }, () => { }, cert);

        // 1 stream: la cancelada y las demas comparten el mismo canal.
        await using var connection = new DnsEndPoint("localhost", serverPort).AsQuicConnection(cert, unaryStreams: 1);
        (await connection.GetAsync<int>(2)).Should().Be(2);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var slow = connection.GetAsync<int>(1, cts.Token).AsTask();

        // Mientras la lenta esta en vuelo, sus vecinas del mismo stream no esperan por ella.
        for (int i = 0; i < 20; i++) (await connection.GetAsync<int>(3)).Should().Be(3);

        await slow.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();

        // La respuesta tardia de la cancelada se descarta; el stream sigue sirviendo.
        release.SetResult();
        await Task.Delay(50);
        for (int i = 0; i < 20; i++) (await connection.GetAsync<int>(4)).Should().Be(4);
    }
}
