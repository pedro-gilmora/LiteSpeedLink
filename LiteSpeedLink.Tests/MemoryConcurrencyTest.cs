using FluentAssertions;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;
using System.Runtime.Versioning;
using Xunit;

namespace LiteSpeedLink.Tests;

public partial class ServersTest
{
    /// <summary>PoC-A en memoria: una instancia de cliente compartida por muchas llamadas en vuelo.</summary>
    [Fact]
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    public async Task TestMemoryConcurrentRoundtrips()
    {
        string MmfName = $"Test-{Guid.CreateVersion7()}";

        using var server = Server.StartMemoryServerAsync(MmfName, HandleAsyncMemoryRequest, () => { }, 5000);
        using var connection = new MemoryConnection(MmfName, 5000);

        var calls = Enumerable.Range(0, 200).Select(n => Task.Run(async () =>
        {
            switch (n % 4)
            {
                case 0:
                    (await connection.GetAsync<(int, int, bool), int>(0, (n, 7, false))).Should().Be(n + 7);
                    break;

                case 1:
                    var payload = $"payload-{n}";
                    connection.Get<string, string>(1, payload).Should().Be(new string([.. payload.Reverse()]));
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
        }));

        await Task.WhenAll(calls);
    }

    /// <summary>Payload mayor que un nodo: la escritura directa desborda y se parte en varios paquetes.</summary>
    [Fact]
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    public async Task TestMemoryMultiPacketPayload()
    {
        string MmfName = $"Test-{Guid.CreateVersion7()}";

        using var server = Server.StartMemoryServerAsync(MmfName, HandleAsyncMemoryRequest, () => { }, 5000);
        using var connection = new MemoryConnection(MmfName, 5000);

        var payload = string.Concat(Enumerable.Range(0, 50_000).Select(i => (char)('a' + i % 26)));

        (await connection.GetAsync<string, string>(1, payload)).Should().Be(new string([.. payload.Reverse()]));
        connection.Get<string, string>(1, "small").Should().Be("llams");
    }
}
