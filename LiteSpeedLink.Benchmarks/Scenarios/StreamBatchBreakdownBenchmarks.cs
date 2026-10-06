using BenchmarkDotNet.Attributes;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace LiteSpeedLink.Benchmarks.Scenarios;

/// <summary>
/// Desglose por causa del coste de un stream de 1000 enteros:
/// <c>Batch=0,Coalesce=false</c> = base (un frame, un handoff y un flush por item);
/// <c>Coalesce=true</c> aisla el handoff por item del lector; <c>Batch&gt;0</c> aisla framing/copias/escrituras;
/// barrer <c>Batch</c> mide el efecto del tamano de lote configurable.
/// </summary>
[MemoryDiagnoser]
[SupportedOSPlatform("windows")]
public class StreamBatchBreakdownBenchmarks
{
    private TcpListener _tcpServer = null!;
    private TcpConnection _tcp = null!;
    private Socket _udsServer = null!;
    private UdsConnection _uds = null!;
#pragma warning disable CA2252 // QUIC es preview: el banco lo acepta a sabiendas
    private System.Net.Quic.QuicListener _quicServer = null!;
    private QuicConnection _quic = null!;

    private const int Items = 1000;

    // int.MaxValue = solo por bytes (32 KB) o cuando el productor va a esperar, como Memory.
    [Params(0, 8, 64, 256, int.MaxValue)]
    public int Batch { get; set; }

    [Params(false, true)]
    public bool Coalesce { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        RequestHandler handler = (op, ctx, _) => ctx.EnumerateAsync(() => Enumerable.Range(0, ctx.Get<int>()));

        _tcpServer = Server.StartTcpServer(0, handler, () => { }, streamBatch: Batch);
        _tcp = new TcpConnection(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)_tcpServer.LocalEndpoint).Port), coalesceStreams: Coalesce);

        var cert = Constants.GetDevCert();
        _quicServer = await Server.StartQuicServerAsync(0, handler, () => { }, cert, default, Batch);
        _quic = new DnsEndPoint("localhost", _quicServer.LocalEndPoint.Port).AsQuicConnection(cert);

        string path = Path.Combine(Path.GetTempPath(), $"Breakdown-{Guid.CreateVersion7():N}.sock");
        _udsServer = Server.StartUdsServer(path, handler, () => { }, streamBatch: Batch);
        _uds = new UdsConnection(path, coalesceStreams: Coalesce);

        await Tcp(); await Uds(); await Quic();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _tcp.DisposeAsync(); _tcpServer.Stop();
        await _uds.DisposeAsync(); _udsServer.Dispose();
        await _quic.DisposeAsync(); await _quicServer.DisposeAsync();
    }

    [Benchmark]
    public Task<int> Tcp() => Drain(_tcp.EnumerateAsync<int, int>(0, Items));

    [Benchmark]
    public Task<int> Uds() => Drain(_uds.EnumerateAsync<int, int>(0, Items));

    // QUIC no tiene lector multiplexado: Coalesce no aplica y sus filas repiten la medicion.
    [Benchmark]
    public Task<int> Quic() => Drain(_quic.EnumerateAsync<int, int>(0, Items));
#pragma warning restore CA2252

    private static async Task<int> Drain(IAsyncEnumerable<int> items)
    {
        int n = 0;
        await foreach (var _ in items) n++;
        return n == Items ? n : throw new InvalidOperationException($"{n} != {Items}");
    }
}
