using BenchmarkDotNet.Attributes;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace LiteSpeedLink.Benchmarks.Scenarios;

/// <summary>6.1 streaming: un stream de <see cref="Items"/> enteros por llamada, por transporte real (Memory, UDS, TCP).</summary>
[MemoryDiagnoser]
[SupportedOSPlatform("windows")]
public class StreamingBenchmarks
{
    private IDisposable _memServer = null!;
    private Socket _udsServer = null!;
    private TcpListener _tcpServer = null!;
    private MemoryConnection _memory = null!;
    private UdsConnection _uds = null!;
    private TcpConnection _tcp = null!;

    [Params(10, 1000)]
    public int Items { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        string name = $"Bench-{Guid.CreateVersion7():N}";
        _memServer = Server.StartMemoryServerAsync(name, (op, ctx, _) => ctx.Yield(Range(ctx.Get<int>())), () => { }, 5000);
        _memory = new MemoryConnection(name, 5000);

        string path = Path.Combine(Path.GetTempPath(), name + ".sock");
        _udsServer = Server.StartUdsServer(path, (op, ctx, _) => ctx.EnumerateAsync(() => Enumerable.Range(0, ctx.Get<int>())), () => { });
        _uds = new UdsConnection(path);

        _tcpServer = Server.StartTcpServer(0, (op, ctx, _) => ctx.EnumerateAsync(() => Enumerable.Range(0, ctx.Get<int>())), () => { });
        _tcp = new TcpConnection(new System.Net.DnsEndPoint("localhost", ((System.Net.IPEndPoint)_tcpServer.LocalEndpoint).Port));

        // Calentar: la primera llamada abre sesion/socket.
        await Memory(); await Uds(); await Tcp();
    }

    private static async IAsyncEnumerable<int> Range(int count)
    {
        for (int i = 0; i < count; i++) yield return i;
        await Task.CompletedTask;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _memory.Dispose(); _memServer.Dispose();
        await _uds.DisposeAsync(); _udsServer.Dispose();
        await _tcp.DisposeAsync(); _tcpServer.Stop();
    }

    [Benchmark(Baseline = true)]
    public Task<int> Memory() => Drain(_memory.EnumerateAsync<int, int>(0, Items));

    [Benchmark]
    public Task<int> Uds() => Drain(_uds.EnumerateAsync<int, int>(0, Items));

    [Benchmark]
    public Task<int> Tcp() => Drain(_tcp.EnumerateAsync<int, int>(0, Items));

    private async Task<int> Drain(IAsyncEnumerable<int> items)
    {
        int n = 0;
        await foreach (var _ in items) n++;
        return n == Items ? n : throw new InvalidOperationException($"{n} != {Items}");
    }
}
