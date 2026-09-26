using BenchmarkDotNet.Attributes;

using LiteSpeedLink.Benchmarks.Tcp;

namespace LiteSpeedLink.Benchmarks.Scenarios;

/// <summary>
/// Throughput de N llamadas concurrentes sobre UNA conexión TCP compartida.
/// El cliente sin protección (A) no se mide aquí: no es correcto, ver <see cref="TcpCorrectnessCheck"/>.
/// </summary>
[MemoryDiagnoser]
public class TcpConcurrencyBenchmarks
{
    private EchoServer _server = null!;
    private IPocClient _serialized = null!, _multiplexed = null!;

    [Params(1, 8, 64)]
    public int Concurrency { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _server = new EchoServer();

        var (s1, t1) = await PocClient.ConnectAsync(_server.Port);
        _serialized = new SerializedClient(s1, t1);

        var (s2, t2) = await PocClient.ConnectAsync(_server.Port);
        _multiplexed = new MultiplexedClient(s2, t2);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _serialized.DisposeAsync();
        await _multiplexed.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public Task Serialized_Semaphore() => Run(_serialized);

    [Benchmark]
    public Task Multiplexed_CorrelationId() => Run(_multiplexed);

    private Task Run(IPocClient client)
    {
        var tasks = new Task[Concurrency];

        for (int i = 0; i < tasks.Length; i++)
            tasks[i] = client.CallAsync("pedro").AsTask();

        return Task.WhenAll(tasks);
    }
}
