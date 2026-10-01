using System.Diagnostics;
using System.Runtime.Versioning;

using FluentAssertions;

using SharedMemory;

using Xunit.Abstractions;

namespace LiteSpeedLink.Tests;

/// <summary>
/// PoC: si el cliente es master (dueño) del RpcBuffer, su Dispose marca Shutdown=1 en el MMF compartido y el lector
/// del servidor (slave) sale solo, sin Bye ni lobby. Mide que se recuperan hilos y que el slave lo nota.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(nameof(MemorySessionLeakPoc))]
public class MemoryOwnerClosePoc(ITestOutputHelper output)
{
    [Fact]
    public void ClientOwnedChannelEndsServerReaderOnDispose()
    {
        const int clients = 20;
        var (handles0, threads0) = Usage();
        var servers = new RpcBuffer[clients];
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < clients; i++)
        {
            string name = $"Owner-{Guid.CreateVersion7():N}";
            using var client = new RpcBuffer(name); // master: crea los MMF
            servers[i] = new RpcBuffer(name, (_, payload) => [(byte)(payload[0] + 1)]); // slave: el servidor se une
            client.RemoteRequest([(byte)i], 2000).Data![0].Should().Be((byte)(i + 1));
        }

        var (handles1, threads1) = Usage();
        bool released = SpinWait.SpinUntil(() => Usage().Threads - threads0 < clients, 5000);
        var (handles2, threads2) = Usage();
        output.WriteLine($"Tras Dispose de {clients} clientes master: hilos +{threads1 - threads0} -> +{threads2 - threads0} en {sw.ElapsedMilliseconds} ms; handles +{handles1 - handles0} -> +{handles2 - handles0}");

        released.Should().BeTrue("el lector slave sale al ver Shutdown=1 del master");
        servers.Should().AllSatisfy(s => s.Invoking(x => x.RemoteRequest([0], 200)).Should().Throw<InvalidOperationException>("el slave ve el canal cerrado por su dueño"));

        foreach (var s in servers) s.Dispose();
        var (handles3, _) = Usage();
        output.WriteLine($"Tras Dispose del lado servidor: handles +{handles3 - handles0}");
    }

    static (int Handles, int Threads) Usage()
    {
        using var p = Process.GetCurrentProcess();
        return (p.HandleCount, p.Threads.Count);
    }
}
