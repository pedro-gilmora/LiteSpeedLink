using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using LiteSpeedLink.Benchmarks.Comparison;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceCrafter.DependencyInjection.Attributes;
using SourceCrafter.LiteSpeedLink;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LiteSpeedLink.Benchmarks.Scenarios;

/// <summary>
/// Cada funcionalidad del contrato generado (TCP loopback) frente a su equivalente en ASP.NET Core slim (HTTP/1.1,
/// binario crudo, OutputCache nativo). Cliente reutilizado y caliente; una categoria = una funcionalidad.
/// <list type="bullet">
/// <item><c>Plain</c>: unaria sin extras (n -> n+1).</item>
/// <item><c>Processor</c>: el cable lleva un token; el host lo resuelve a Principal (ServerProcessor / lookup en el endpoint).</item>
/// <item><c>ClientRetry</c>/<c>ServerRetry</c>: coste del bucle de reintento en el camino feliz.</item>
/// <item><c>ClientCacheHit</c>: acierto en cliente (no viaja) frente a OutputCache (acierto en servidor, si viaja).</item>
/// <item><c>ServerCacheHit</c>/<c>CacheKeyHit</c>: acierto en el host frente a OutputCache (VaryByQuery = CacheKey).</item>
/// <item><c>SingleFlight</c>: 16 llamadas identicas concurrentes en frio: 1 viaje frente a 16.</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class FeatureBenchmarks
{
    private const int Burst = 16;
    private const string Token = "tok-admin";
    private static readonly FeatureQuote Quote = new(7, "a");

    private TcpListener _host = null!;
    private FeatureServiceClient.FeaturesClient _lsl = null!;
    private WebApplication _slim = null!;
    private HttpClient _http = null!;
    private int _burstKey;

    [GlobalSetup]
    public async Task Setup()
    {
        _host = FeatureService.Start(0, null);
        _lsl = new FeatureServiceClient("127.0.0.1", ((IPEndPoint)_host.LocalEndpoint).Port).Features;

        int p = ComparisonBenchmarks.FreePort();
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Listen(IPAddress.Loopback, p, o => o.Protocols = HttpProtocols.Http1); });
        b.Services.AddOutputCache();
        _slim = b.Build();
        _slim.UseOutputCache();
        MapSlim(_slim);
        await _slim.StartAsync();
        _http = ComparisonBenchmarks.Http(p, HttpVersion.Version11);

        // Calentar y validar la semantica: los aciertos de cache no ejecutan el handler.
        // Deltas: MemoryRandomization repite GlobalSetup en el mismo proceso (estaticos acumulados; la cache de host puede seguir caliente).
        int price = FeaturesImpl.PriceCalls, stock = FeaturesImpl.StockCalls, quote = FeaturesImpl.QuoteCalls;
        int sPrice = SlimCounters.Price, sStock = SlimCounters.Stock, sQuote = SlimCounters.Quote;
        for (int i = 0; i < 3; i++)
        {
            await Lsl_Plain(); await AspNetSlim_Plain();
            await Lsl_Processor(); await AspNetSlim_Processor();
            await Lsl_ClientRetry(); await AspNetSlim_ClientRetry();
            await Lsl_ServerRetry(); await AspNetSlim_ServerRetry();
            await Lsl_ClientCacheHit(); await AspNetSlim_ClientCacheHit();
            await Lsl_ServerCacheHit(); await AspNetSlim_ServerCacheHit();
            await Lsl_CacheKeyHit(); await AspNetSlim_CacheKeyHit();
            Check(await _lsl.QuoteAsync(Quote with { Note = $"x{i}" }), "7:a");
            Check(await GetText($"/quote?sku=7&note=x{i}"), "7:a");
        }

        var burst = FeaturesImpl.BurstCalls;
        await Lsl_SingleFlight();
        await AspNetSlim_SingleFlight();

        Check(FeaturesImpl.PriceCalls - price, 1);
        Check(FeaturesImpl.StockCalls - stock <= 1, true);
        Check(FeaturesImpl.QuoteCalls - quote <= 1, true);
        Check(FeaturesImpl.BurstCalls - burst, 1);
        Check(SlimCounters.Price - sPrice, 1);
        Check(SlimCounters.Stock - sStock, 1);
        Check(SlimCounters.Quote - sQuote, 1);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _host.Stop();
        _http.Dispose();
        await _slim.DisposeAsync();
    }

    // ---------- Plain ----------

    [Benchmark(Baseline = true), BenchmarkCategory("Plain")]
    public async Task<int> Lsl_Plain() => Check(await _lsl.PlainAsync(1), 2);

    [Benchmark, BenchmarkCategory("Plain")]
    public async Task<int> AspNetSlim_Plain() => Check(await PostInt("/plain", 1), 2);

    // ---------- Processor (autenticacion) ----------

    [Benchmark(Baseline = true), BenchmarkCategory("Processor")]
    public async Task<string> Lsl_Processor() => Check(await _lsl.WhoAmIAsync(Token), "pedro");

    [Benchmark, BenchmarkCategory("Processor")]
    public async Task<string> AspNetSlim_Processor()
    {
        using var c = new ByteArrayContent(Encoding.UTF8.GetBytes(Token));
        using var r = await _http.PostAsync("/whoami", c);
        return Check(await r.Content.ReadAsStringAsync(), "pedro");
    }

    // ---------- Retry (camino feliz) ----------

    [Benchmark(Baseline = true), BenchmarkCategory("ClientRetry")]
    public async Task<int> Lsl_ClientRetry() => Check(await _lsl.ClientRetriedAsync(1), 2);

    [Benchmark, BenchmarkCategory("ClientRetry")]
    public async Task<int> AspNetSlim_ClientRetry()
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return Check(await PostInt("/plain", 1), 2); }
            catch (TaskCanceledException) when (attempt < 3) { }
        }
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ServerRetry")]
    public async Task<int> Lsl_ServerRetry() => Check(await _lsl.ServerRetriedAsync(1), 2);

    [Benchmark, BenchmarkCategory("ServerRetry")]
    public async Task<int> AspNetSlim_ServerRetry() => Check(await PostInt("/retry-server", 1), 2);

    // ---------- Cache ----------

    [Benchmark(Baseline = true), BenchmarkCategory("ClientCacheHit")]
    public async Task<int> Lsl_ClientCacheHit() => Check(await _lsl.PriceAsync(7), 70);

    [Benchmark, BenchmarkCategory("ClientCacheHit")]
    public async Task<int> AspNetSlim_ClientCacheHit() => Check(await GetInt("/price/7"), 70);

    [Benchmark(Baseline = true), BenchmarkCategory("ServerCacheHit")]
    public async Task<int> Lsl_ServerCacheHit() => Check(await _lsl.StockAsync(7), 8);

    [Benchmark, BenchmarkCategory("ServerCacheHit")]
    public async Task<int> AspNetSlim_ServerCacheHit() => Check(await GetInt("/stock/7"), 8);

    [Benchmark(Baseline = true), BenchmarkCategory("CacheKeyHit")]
    public async Task<string> Lsl_CacheKeyHit() => Check(await _lsl.QuoteAsync(Quote), "7:a");

    [Benchmark, BenchmarkCategory("CacheKeyHit")]
    public async Task<string> AspNetSlim_CacheKeyHit() => Check(await GetText("/quote?sku=7&note=a"), "7:a");

    // ---------- Single-flight: 16 llamadas identicas en frio ----------

    [Benchmark(Baseline = true), BenchmarkCategory("SingleFlight")]
    public async Task<int> Lsl_SingleFlight()
    {
        int key = Interlocked.Increment(ref _burstKey);
        var tasks = new Task<int>[Burst];
        for (int i = 0; i < Burst; i++) tasks[i] = _lsl.BurstAsync(key).AsTask();
        foreach (var v in await Task.WhenAll(tasks)) Check(v, key);
        return key;
    }

    [Benchmark, BenchmarkCategory("SingleFlight")]
    public async Task<int> AspNetSlim_SingleFlight()
    {
        int key = Interlocked.Increment(ref _burstKey);
        var tasks = new Task<int>[Burst];
        for (int i = 0; i < Burst; i++) tasks[i] = PostInt("/burst", key);
        foreach (var v in await Task.WhenAll(tasks)) Check(v, key);
        return key;
    }

    // ---------- ASP.NET Core slim ----------

    private static void MapSlim(WebApplication app)
    {
        app.MapPost("/plain", static async ctx => await WriteInt(ctx.Response, await ReadInt(ctx.Request) + 1));
        app.MapPost("/burst", static async ctx => await WriteInt(ctx.Response, await ReadInt(ctx.Request)));
        app.MapPost("/whoami", static async ctx =>
        {
            using var sr = new StreamReader(ctx.Request.Body);
            if (FeatureTokens.Instance.Find(await sr.ReadToEndAsync()) is not { } p) { ctx.Response.StatusCode = 401; return; }
            await ctx.Response.WriteAsync(p.User);
        });
        app.MapPost("/retry-server", static async ctx =>
        {
            int n = await ReadInt(ctx.Request), v;
            for (var attempt = 1; ; attempt++)
            {
                try { v = n + 1; break; }
                catch (TimeoutException) when (attempt < 3) { }
            }
            await WriteInt(ctx.Response, v);
        });
        app.MapGet("/price/{sku:int}", static (HttpContext ctx, int sku) => { Interlocked.Increment(ref SlimCounters.Price); return WriteInt(ctx.Response, sku * 10); }).CacheOutput();
        app.MapGet("/stock/{sku:int}", static (HttpContext ctx, int sku) => { Interlocked.Increment(ref SlimCounters.Stock); return WriteInt(ctx.Response, sku + 1); }).CacheOutput();
        app.MapGet("/quote", static (HttpContext ctx, int sku, string note) =>
        {
            Interlocked.Increment(ref SlimCounters.Quote);
            return ctx.Response.WriteAsync($"{sku}:{note}");
        }).CacheOutput(o => o.SetVaryByQuery("sku"));
    }

    private static async Task<int> ReadInt(HttpRequest req)
    {
        var r = await req.BodyReader.ReadAtLeastAsync(4);
        int v = Int(r.Buffer);
        req.BodyReader.AdvanceTo(r.Buffer.GetPosition(4));
        return v;
    }

    private static int Int(ReadOnlySequence<byte> buffer)
    {
        Span<byte> tmp = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(tmp);
        return BinaryPrimitives.ReadInt32LittleEndian(tmp);
    }

    private static Task WriteInt(HttpResponse res, int v)
    {
        res.ContentLength = 4;
        BinaryPrimitives.WriteInt32LittleEndian(res.BodyWriter.GetSpan(4), v);
        res.BodyWriter.Advance(4);
        return res.BodyWriter.FlushAsync().AsTask();
    }

    private async Task<int> PostInt(string path, int v)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, v);
        using var c = new ByteArrayContent(buf);
        using var r = await _http.PostAsync(path, c);
        return BinaryPrimitives.ReadInt32LittleEndian(await r.Content.ReadAsByteArrayAsync());
    }

    private async Task<int> GetInt(string path)
    {
        using var r = await _http.GetAsync(path);
        return BinaryPrimitives.ReadInt32LittleEndian(await r.Content.ReadAsByteArrayAsync());
    }

    private Task<string> GetText(string path) => _http.GetStringAsync(path);

    private static T Check<T>(T v, T expected) =>
        EqualityComparer<T>.Default.Equals(v, expected) ? v : throw new InvalidOperationException($"{v} != {expected}");
}

internal static class SlimCounters
{
    public static int Price, Stock, Quote;
}

// ---------- Contrato generado ----------

public sealed record FeaturePrincipal(string User, string Role);

[MemoryPack.MemoryPackable]
public sealed partial record FeatureQuote(int Sku, string Note);

public interface IFeatures : IServiceUnit
{
    int Plain(int n);

    string WhoAmI([ServerProcessor<FeatureAuth>] FeaturePrincipal caller);

    [ClientRetry(3)]
    int ClientRetried(int n);

    [ServerRetry(3)]
    int ServerRetried(int n);

    [ClientCache(60_000)]
    int Price(int sku);

    // TTL minimo: cada rafaga usa clave nueva, asi que solo mide el single-flight en frio.
    [ClientCache(1)]
    int Burst(int n);

    [ServerCache(60_000)]
    int Stock(int sku);

    [ServerCache(60_000)]
    string Quote([CacheKey(nameof(FeatureQuote.Sku))] FeatureQuote query);
}

public sealed class FeatureTokens
{
    public static readonly FeatureTokens Instance = new();
    private readonly Dictionary<string, FeaturePrincipal> _tokens = new() { ["tok-admin"] = new("pedro", "admin") };

    public FeaturePrincipal? Find(string token) => _tokens.GetValueOrDefault(token);
}

public sealed class FeatureAuth(FeatureTokens store) : IPipeline<string, FeaturePrincipal>
{
    public (ResponseStatus, FeaturePrincipal) Process(string token) =>
        store.Find(token) is { } p ? (ResponseStatus.Success, p) : (ResponseStatus.Failed, null!);
}

public sealed class FeaturesImpl : IFeatures
{
    public static int PriceCalls, StockCalls, QuoteCalls, BurstCalls;

    public int Plain(int n) => n + 1;
    public string WhoAmI(FeaturePrincipal caller) => caller.User;
    public int ClientRetried(int n) => n + 1;
    public int ServerRetried(int n) => n + 1;

    public int Price(int sku)
    {
        Interlocked.Increment(ref PriceCalls);
        return sku * 10;
    }

    public int Burst(int n)
    {
        Interlocked.Increment(ref BurstCalls);
        return n;
    }

    public int Stock(int sku)
    {
        Interlocked.Increment(ref StockCalls);
        return sku + 1;
    }

    public string Quote(FeatureQuote query)
    {
        Interlocked.Increment(ref QuoteCalls);
        return $"{query.Sku}:{query.Note}";
    }
}

[ServiceHost(ServiceConnectionType.Tcp)]
[ServiceProvider]
[Singleton<FeatureTokens>]
[Singleton<FeatureAuth>]
[Scoped<IFeatures, FeaturesImpl>]
public partial class FeatureService;

[ServiceClient(ServiceConnectionType.Tcp)]
[ServiceUnit<IFeatures>]
[ServiceProvider]
public sealed partial class FeatureServiceClient;
