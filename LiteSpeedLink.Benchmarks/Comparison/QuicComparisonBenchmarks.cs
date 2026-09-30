using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;
using System.Net;
using System.Net.Quic;
using System.Runtime.Versioning;
using static LiteSpeedLink.Benchmarks.Comparison.ComparisonBenchmarks;

namespace LiteSpeedLink.Benchmarks.Comparison;

/// <summary>
/// Par de <see cref="ComparisonBenchmarks"/> sobre QUIC loopback: LiteSpeedLink QUIC frente a ASP.NET Core slim en
/// HTTP/3 (Kestrel/msquic), mismos endpoints y misma validación. Ambos con TLS 1.3 y el mismo certificado de desarrollo.
/// </summary>
[MemoryDiagnoser]
[SupportedOSPlatform("windows")]
#pragma warning disable CA2252 // QUIC es preview: el banco lo acepta a sabiendas
public class QuicComparisonBenchmarks
{
    private QuicListener _lslServer = null!, _lslBatchServer = null!;
    private SourceCrafter.LiteSpeedLink.Client.QuicConnection _lsl = null!, _lslBatched = null!;
    private WebApplication _slim = null!;
    private HttpClient _slimClient = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        if (!QuicListener.IsSupported) throw new PlatformNotSupportedException("QUIC no disponible.");
        var cert = Constants.GetDevCert();

        _lslServer = await Server.StartQuicServerAsync(0, static (op, ctx, _) => op == 0
            ? ctx.ReturnAsync(ctx.Get<(int, int)>().Item1 + ctx.Get<(int, int)>().Item2)
            : ctx.EnumerateAsync(() => Enumerable.Range(0, ctx.Get<int>())), () => { }, cert);
        _lsl = new DnsEndPoint("localhost", _lslServer.LocalEndPoint.Port).AsQuicConnection(cert);

        _lslBatchServer = await Server.StartQuicServerAsync(0, static (op, ctx, _) => ctx.EnumerateAsync(() => Enumerable.Range(0, ctx.Get<int>())), () => { }, cert, default, StreamBatch);
        _lslBatched = new DnsEndPoint("localhost", _lslBatchServer.LocalEndPoint.Port).AsQuicConnection(cert);

        int p;
        using (var probe = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) p = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseQuic();
        b.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Listen(IPAddress.Loopback, p, o => { o.Protocols = HttpProtocols.Http3; o.UseHttps(cert); }); });
        _slim = b.Build();
        MapSlim(_slim);
        await _slim.StartAsync();
        _slimClient = new(new SocketsHttpHandler
        {
            UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
            SslOptions = { RemoteCertificateValidationCallback = static (_, _, _, _) => true }
        })
        {
            BaseAddress = new Uri($"https://127.0.0.1:{p}"), // localhost resuelve a ::1 y Kestrel solo escucha en 127.0.0.1
            DefaultRequestVersion = HttpVersion.Version30,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        await Lsl_Unary(); await AspNetSlimH3_Unary();
        await Lsl_Stream(); await AspNetSlimH3_Stream();
        await Lsl_StreamBatched(); await AspNetSlimH3_StreamBatched();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _lsl.DisposeAsync(); await _lslBatched.DisposeAsync();
        await _lslServer.DisposeAsync(); await _lslBatchServer.DisposeAsync();
        _slimClient.Dispose(); await _slim.DisposeAsync();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Unary")]
    public async Task<int> Lsl_Unary() => Sum(await _lsl.GetAsync<(int, int), int>(0, (1, 2)));

    [Benchmark, BenchmarkCategory("Unary")]
    public Task<int> AspNetSlimH3_Unary() => SlimUnary(_slimClient);

    [Benchmark, BenchmarkCategory("Stream")]
    public async Task<int> Lsl_Stream()
    {
        int n = 0;
        await foreach (var i in _lsl.EnumerateAsync<int, int>(1, Items)) if (i == n) n++;
        return Count(n);
    }

    [Benchmark, BenchmarkCategory("Stream")]
    public Task<int> AspNetSlimH3_Stream() => SlimStream(_slimClient, $"/range/{Items}");

    [Benchmark, BenchmarkCategory("Stream")]
    public async Task<int> Lsl_StreamBatched()
    {
        int n = 0;
        await foreach (var i in _lslBatched.EnumerateAsync<int, int>(1, Items)) if (i == n) n++;
        return Count(n);
    }

    [Benchmark, BenchmarkCategory("Stream")]
    public Task<int> AspNetSlimH3_StreamBatched() => SlimStream(_slimClient, $"/range-batched/{Items}");
}
