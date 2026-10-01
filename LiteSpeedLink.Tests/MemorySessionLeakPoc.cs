using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;

using FluentAssertions;

using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;

using Xunit.Abstractions;

namespace LiteSpeedLink.Tests;

/// <summary>
/// PoC del paso 8: el servidor Memory es un solo <c>RpcBuffer.Host</c> multicliente; cada cliente que hace Dispose
/// envia Close y el host libera su anillo de respuestas (sin lobby, Bye ni hilo lector por cliente).
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(nameof(MemorySessionLeakPoc))]
public class MemorySessionLeakPoc(ITestOutputHelper output)
{
    [Fact]
    public void DisposedClientsReleaseServerSessions()
    {
        const int clients = 20;
        string name = $"Test-{Guid.CreateVersion7()}";
        var server = Server.StartMemoryServer(name, (op, ctx, token) => ctx.Return(ctx.Get<int>() + 1), () => { });
        var rpc = server.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance).Single(f => f.FieldType == typeof(SharedMemory.RpcBuffer)).GetValue(server)!;
        var sessions = (ICollection)rpc.GetType().GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(rpc)!;
        var (handles0, threads0) = Usage();

        for (int i = 0; i < clients; i++)
        {
            using var client = new MemoryConnection(name, 2000);
            client.Get<int, int>(0, i).Should().Be(i + 1);
        }

        SpinWait.SpinUntil(() => sessions.Count == 0, 2000); // el Close lo atiende el lector del host
        SpinWait.SpinUntil(() => Usage().Threads - threads0 < clients, 5000); // los lectores de cliente salen en su Dispose
        var (handles1, threads1) = Usage();
        output.WriteLine($"{clients} clientes conectados y liberados -> clientes vivos en el host: {sessions.Count}, handles +{handles1 - handles0}, hilos +{threads1 - threads0}");

        sessions.Count.Should().Be(0, "cada Dispose de cliente libera su anillo en el host");
        (threads1 - threads0).Should().BeLessThan(clients, "los clientes liberados no retienen hilo lector");
        server.Dispose();
    }

    static (int Handles, int Threads) Usage()
    {
        using var p = Process.GetCurrentProcess();
        return (p.HandleCount, p.Threads.Count);
    }
}

/// <summary>Hilos y handles son globales al proceso: el PoC corre aislado del resto.</summary>
[CollectionDefinition(nameof(MemorySessionLeakPoc), DisableParallelization = true)]
public class MemorySessionLeakPocCollection;
