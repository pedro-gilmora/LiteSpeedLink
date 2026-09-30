using System.Net;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;
using Xunit;

namespace LiteSpeedLink.Tests;

public class DisposeTest
{
    /// <summary>5.6: con el servidor vivo, DisposeAsync no cuelga (el lector se desbloquea al cerrar el socket antes que el canal).</summary>
    [Fact]
    public async Task TcpDisposeDoesNotHang()
    {
        var server = Server.StartTcpServer(0, (op, ctx, _) => ctx.ReturnAsync(ctx.Get<int>()), () => { });
        try
        {
            var client = new TcpConnection(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)server.LocalEndpoint).Port));
            await AssertDisposeAsync(client);
        }
        finally { server.Stop(); }
    }

    [Fact]
    public async Task UdsDisposeDoesNotHang()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lsl-{Guid.NewGuid():N}.sock");
        using var uds = Server.StartUdsServer(path, (op, ctx, _) => ctx.ReturnAsync(ctx.Get<int>()), () => { });
        await AssertDisposeAsync(new UdsConnection(path));
    }

    private static async Task AssertDisposeAsync(StreamConnection client)
    {
        Assert.Equal(7, await client.GetAsync<int, int>(0, 7, new CancellationTokenSource(3000).Token));
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }
}
