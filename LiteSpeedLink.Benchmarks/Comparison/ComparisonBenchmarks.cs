using BenchmarkDotNet.Attributes;
using Grpc.Core;
using Grpc.Net.Client;
using LiteSpeedLink.Benchmarks.Comparison.Grpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace LiteSpeedLink.Benchmarks.Comparison;

/// <summary>
/// Comparativa plana: todas las filas hacen EXACTAMENTE lo mismo sobre TCP loopback (127.0.0.1), un cliente
/// reutilizado y ya calentado:
/// <list type="bullet">
/// <item><c>*_Unary</c>: envía (1, 2), el servidor devuelve 3.</item>
/// <item><c>*_Stream</c>: envía 1000, el servidor emite 0..999 y el cliente los cuenta.</item>
/// </list>
/// Cada competidor con su serialización natural y sus optimizaciones habituales (logging fuera, sin cabecera Server).
/// Sin parámetros ni ramas: una fila = un stack.
/// </summary>
[MemoryDiagnoser]
[SupportedOSPlatform("windows")]
public class ComparisonBenchmarks
{
    private const int Items = 1000;
    private static readonly AddRequest GrpcAdd = new() { A = 1, B = 2 };
    private static readonly RangeRequest GrpcRange = new() { Count = Items };
    private static readonly byte[] RawAdd = [1, 0, 0, 0, 2, 0, 0, 0];

    private TcpListener _lslServer = null!;
    private TcpConnection _lsl = null!;
    private WebApplication _aspnet = null!, _aspnetSlim = null!, _grpc = null!, _grpcSlim = null!;
    private HttpClient _aspnetClient = null!, _aspnetSlimClient = null!;
    private GrpcChannel _grpcChannel = null!, _grpcSlimChannel = null!;
    private Bench.BenchClient _grpcClient = null!, _grpcSlimClient = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        // LiteSpeedLink TCP: opId 0 = suma, opId 1 = rango.
        _lslServer = Server.StartTcpServer(0, static (op, ctx, _) => op == 0
            ? ctx.ReturnAsync(ctx.Get<(int, int)>().Item1 + ctx.Get<(int, int)>().Item2)
            : ctx.EnumerateAsync(() => Enumerable.Range(0, ctx.Get<int>())), () => { });
        _lsl = new TcpConnection(new DnsEndPoint("localhost", ((IPEndPoint)_lslServer.LocalEndpoint).Port));

        // ASP.NET Core completo: CreateBuilder + minimal API + JSON source-gen, HTTP/1.1.
        int p = FreePort();
        var b = WebApplication.CreateBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Listen(IPAddress.Loopback, p, o => o.Protocols = HttpProtocols.Http1); });
        b.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, CmpJson.Default));
        _aspnet = b.Build();
        _aspnet.MapPost("/add", (AddDto d) => d.A + d.B);
        _aspnet.MapGet("/range/{n:int}", (int n) => Enumerable.Range(0, n));
        await _aspnet.StartAsync();
        _aspnetClient = Http(p, HttpVersion.Version11);

        // ASP.NET Core slim: CreateSlimBuilder + binario crudo en BodyReader/BodyWriter, HTTP/1.1.
        p = FreePort();
        b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Listen(IPAddress.Loopback, p, o => o.Protocols = HttpProtocols.Http1); });
        _aspnetSlim = b.Build();
        _aspnetSlim.MapPost("/add", static async ctx =>
        {
            var r = await ctx.Request.BodyReader.ReadAtLeastAsync(8);
            Span<byte> tmp = stackalloc byte[8];
            System.Buffers.BuffersExtensions.CopyTo(r.Buffer.Slice(0, 8), tmp);
            ctx.Request.BodyReader.AdvanceTo(r.Buffer.GetPosition(8));
            ctx.Response.ContentLength = 4;
            BinaryPrimitives.WriteInt32LittleEndian(ctx.Response.BodyWriter.GetSpan(4), BinaryPrimitives.ReadInt32LittleEndian(tmp) + BinaryPrimitives.ReadInt32LittleEndian(tmp[4..]));
            ctx.Response.BodyWriter.Advance(4);
            await ctx.Response.BodyWriter.FlushAsync();
        });
        _aspnetSlim.MapGet("/range/{n:int}", static async ctx =>
        {
            int n = int.Parse((string)ctx.Request.RouteValues["n"]!);
            var w = ctx.Response.BodyWriter;
            ctx.Response.ContentLength = n * 4L;
            for (int i = 0; i < n; i++) { BinaryPrimitives.WriteInt32LittleEndian(w.GetSpan(4), i); w.Advance(4); }
            await w.FlushAsync();
        });
        await _aspnetSlim.StartAsync();
        _aspnetSlimClient = Http(p, HttpVersion.Version11);

        // gRPC completo: CreateBuilder + Grpc.AspNetCore, HTTP/2 h2c.
        p = FreePort();
        b = WebApplication.CreateBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Listen(IPAddress.Loopback, p, o => o.Protocols = HttpProtocols.Http2); });
        b.Services.AddGrpc();
        _grpc = b.Build();
        _grpc.MapGrpcService<BenchService>();
        await _grpc.StartAsync();
        _grpcChannel = GrpcChannel.ForAddress($"http://127.0.0.1:{p}", new GrpcChannelOptions { HttpClient = Http(p, HttpVersion.Version20), DisposeHttpClient = true });
        _grpcClient = new(_grpcChannel);

        // gRPC slim: CreateSlimBuilder + Grpc.AspNetCore, HTTP/2 h2c.
        p = FreePort();
        b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Listen(IPAddress.Loopback, p, o => o.Protocols = HttpProtocols.Http2); });
        b.Services.AddGrpc();
        _grpcSlim = b.Build();
        _grpcSlim.MapGrpcService<BenchService>();
        await _grpcSlim.StartAsync();
        _grpcSlimChannel = GrpcChannel.ForAddress($"http://127.0.0.1:{p}", new GrpcChannelOptions { HttpClient = Http(p, HttpVersion.Version20), DisposeHttpClient = true });
        _grpcSlimClient = new(_grpcSlimChannel);

        // Calentar todo (y validar) antes de medir.
        await Lsl_Unary(); await AspNet_Unary(); await AspNetSlim_Unary(); await Grpc_Unary(); await GrpcSlim_Unary();
        await Lsl_Stream(); await AspNet_Stream(); await AspNetSlim_Stream(); await Grpc_Stream(); await GrpcSlim_Stream();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _lsl.DisposeAsync(); _lslServer.Stop();
        _aspnetClient.Dispose(); _aspnetSlimClient.Dispose(); _grpcChannel.Dispose(); _grpcSlimChannel.Dispose();
        await _aspnet.DisposeAsync(); await _aspnetSlim.DisposeAsync(); await _grpc.DisposeAsync(); await _grpcSlim.DisposeAsync();
    }

    // ---------- Unaria: (1, 2) -> 3 ----------

    [Benchmark(Baseline = true), BenchmarkCategory("Unary")]
    public async Task<int> Lsl_Unary() => Sum(await _lsl.GetAsync<(int, int), int>(0, (1, 2)));

    [Benchmark, BenchmarkCategory("Unary")]
    public async Task<int> AspNet_Unary()
    {
        using var r = await _aspnetClient.PostAsJsonAsync("/add", new AddDto(1, 2), CmpJson.Default.AddDto);
        return Sum(await r.Content.ReadFromJsonAsync(CmpJson.Default.Int32));
    }

    [Benchmark, BenchmarkCategory("Unary")]
    public async Task<int> AspNetSlim_Unary()
    {
        using var content = new ByteArrayContent(RawAdd);
        using var r = await _aspnetSlimClient.PostAsync("/add", content);
        return Sum(BinaryPrimitives.ReadInt32LittleEndian(await r.Content.ReadAsByteArrayAsync()));
    }

    [Benchmark, BenchmarkCategory("Unary")]
    public async Task<int> Grpc_Unary() => Sum((await _grpcClient.AddAsync(GrpcAdd)).Sum);

    [Benchmark, BenchmarkCategory("Unary")]
    public async Task<int> GrpcSlim_Unary() => Sum((await _grpcSlimClient.AddAsync(GrpcAdd)).Sum);

    // ---------- Stream: 1000 enteros ----------

    [Benchmark, BenchmarkCategory("Stream")]
    public async Task<int> Lsl_Stream()
    {
        int n = 0;
        await foreach (var _ in _lsl.EnumerateAsync<int, int>(1, Items)) n++;
        return Count(n);
    }

    [Benchmark, BenchmarkCategory("Stream")]
    public async Task<int> AspNet_Stream()
    {
        int n = 0;
        await foreach (var _ in _aspnetClient.GetFromJsonAsAsyncEnumerable($"/range/{Items}", CmpJson.Default.Int32)) n++;
        return Count(n);
    }

    [Benchmark, BenchmarkCategory("Stream")]
    public async Task<int> AspNetSlim_Stream()
    {
        using var r = await _aspnetSlimClient.GetAsync($"/range/{Items}", HttpCompletionOption.ResponseHeadersRead);
        await using var s = await r.Content.ReadAsStreamAsync();
        var buf = new byte[4096];
        long bytes = 0;
        int read;
        while ((read = await s.ReadAsync(buf)) > 0) bytes += read;
        return Count((int)(bytes / 4));
    }

    [Benchmark, BenchmarkCategory("Stream")]
    public async Task<int> Grpc_Stream()
    {
        using var call = _grpcClient.Range(GrpcRange);
        int n = 0;
        while (await call.ResponseStream.MoveNext(CancellationToken.None)) n++;
        return Count(n);
    }

    [Benchmark, BenchmarkCategory("Stream")]
    public async Task<int> GrpcSlim_Stream()
    {
        using var call = _grpcSlimClient.Range(GrpcRange);
        int n = 0;
        while (await call.ResponseStream.MoveNext(CancellationToken.None)) n++;
        return Count(n);
    }

    private static int Sum(int v) => v == 3 ? v : throw new InvalidOperationException($"sum {v}");
    private static int Count(int n) => n == Items ? n : throw new InvalidOperationException($"{n} != {Items}");

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static HttpClient Http(int port, Version version) => new(new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false })
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        DefaultRequestVersion = version,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
    };
}

public sealed record AddDto(int A, int B);

[System.Text.Json.Serialization.JsonSerializable(typeof(AddDto))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
[System.Text.Json.Serialization.JsonSerializable(typeof(IEnumerable<int>))]
internal partial class CmpJson : System.Text.Json.Serialization.JsonSerializerContext;

internal sealed class BenchService : Bench.BenchBase
{
    public override Task<AddReply> Add(AddRequest request, ServerCallContext context) =>
        Task.FromResult(new AddReply { Sum = request.A + request.B });

    public override async Task Range(RangeRequest request, IServerStreamWriter<Item> responseStream, ServerCallContext context)
    {
        responseStream.WriteOptions = new WriteOptions(WriteFlags.BufferHint);
        for (int i = 0; i < request.Count; i++) await responseStream.WriteAsync(new Item { Value = i });
    }
}
