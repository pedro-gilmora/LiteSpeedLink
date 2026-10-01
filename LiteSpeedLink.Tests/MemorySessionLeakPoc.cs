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
/// PoC del paso 8: el servidor Memory vive lo que su proceso, pero cada cliente que se va (aun con Dispose)
/// deja su sesion (RpcBuffer: MMF + eventos + hilo lector) viva hasta que se libera el servidor.
/// Si este test falla porque las sesiones bajan, el paso 8 esta resuelto: invertir el aserto.
/// </summary>
[SupportedOSPlatform("windows")]
public class MemorySessionLeakPoc(ITestOutputHelper output)
{
    [Fact]
    public void DisposedClientsKeepServerSessionsAlive()
    {
        const int clients = 20;
        string name = $"Test-{Guid.CreateVersion7()}";
        var server = Server.StartMemoryServer(name, (op, ctx, token) => ctx.Return(ctx.Get<int>() + 1), () => { });
        var sessions = (ICollection)server.GetType().GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(server)!;
        var (handles0, threads0) = Usage();

        for (int i = 0; i < clients; i++)
        {
            using var client = new MemoryConnection(name, 2000);
            client.Get<int, int>(0, i).Should().Be(i + 1);
        }

        Thread.Sleep(500); // margen para que el servidor reaccionara si detectara la salida
        var (handles1, threads1) = Usage();
        output.WriteLine($"{clients} clientes conectados y liberados -> sesiones vivas: {sessions.Count}, handles +{handles1 - handles0}, hilos +{threads1 - threads0}");

        sessions.Count.Should().Be(clients, "ningun Dispose de cliente libera su sesion en el servidor");
        (threads1 - threads0).Should().BeGreaterThanOrEqualTo(clients, "cada sesion retiene su hilo lector");

        server.Dispose();
        Thread.Sleep(500);
        var (handles2, threads2) = Usage();
        output.WriteLine($"Tras liberar el servidor -> handles +{handles2 - handles0}, hilos +{threads2 - threads0}");
        (threads2 - threads0).Should().BeLessThan(clients, "solo liberar el servidor recupera los hilos");
    }

    static (int Handles, int Threads) Usage()
    {
        using var p = Process.GetCurrentProcess();
        return (p.HandleCount, p.Threads.Count);
    }
}
