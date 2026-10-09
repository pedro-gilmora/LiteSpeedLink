using FluentAssertions;

namespace LiteSpeedLink.Tests;

/// <summary>[ServerCache]: consulta/single-flight/guardado en el host generado y SCLSL017.</summary>
public class ServerCacheGeneratorTest
{
    const string Source = """
        using SourceCrafter.DependencyInjection.Attributes;
        using SourceCrafter.LiteSpeedLink;

        namespace Probe;

        public sealed class Trim : IPipeline<string>
        {
            public (ResponseStatus, string) Process(string input) => (ResponseStatus.Success, input.Trim());
        }

        public interface IContract : IServiceUnit
        {
            [ServerCache(5000, 16)] int Hit(int n);
            [ServerCache(5000)] System.Threading.Tasks.Task<string> EchoAsync(string s);
            [ServerCache(5000)] string Shout([ServerProcessor<Trim>] string s);
            [ServerCache(5000)] void Ping();
            [ServerCache(5000)] bool TryGet(int key, out string value);
            [ServerCache(0)] int Zero(int n);
            int Plain(int n);
        }

        public sealed class Impl : IContract
        {
            public int Hit(int n) => n;
            public System.Threading.Tasks.Task<string> EchoAsync(string s) => System.Threading.Tasks.Task.FromResult(s);
            public string Shout(string s) => s;
            public void Ping() { }
            public bool TryGet(int key, out string value) { value = ""; return true; }
            public int Zero(int n) => n;
            public int Plain(int n) => n;
        }

        [ServiceHost(ServiceConnectionType.CONN)]
        [ServiceProvider]
        [Singleton<Trim>]
        [Scoped<IContract, Impl>]
        public partial class Host;
        """;

    // Bloque del case de un metodo: desde su "case" hasta el siguiente.
    static string Case(string host, string method)
    {
        var start = host.LastIndexOf("case ", host.IndexOf("IContract." + method + "("));
        var end = host.IndexOf("case ", start + 5);
        return host[start..(end < 0 ? host.IndexOf("default:", start) : end)];
    }

    [Theory]
    [InlineData("Tcp", "await __context.ReturnRawAsync(__cached)")]
    [InlineData("Udp", "await __context.ReturnRawAsync(__cached)")]
    [InlineData("Memory", "__context.ReturnRaw(__cached)")]
    public void HostLooksUpSingleFlightsAndReturnsRawBytes(string conn, string rawReturn)
    {
        var r = GeneratorHarness.Run(Source.Replace("CONN", conn));

        r.Errors.Should().BeEmpty();
        var host = r.Sources.Single(s => s.Key.Contains(".host")).Value;

        // Un manager perezoso y un escritor de respuesta por metodo cacheado (Hit, EchoAsync, Shout).
        host.Split("LazyInitializer.EnsureInitialized(").Length.Should().Be(4);
        host.Should().Contain("new global::SourceCrafter.LiteSpeedLink.ServerCacheManager(5000, 16)")
            .And.Contain("new global::SourceCrafter.LiteSpeedLink.ServerCacheManager(5000, 1024)");
        host.Split("var __cacheKey = __context.Body;").Length.Should().Be(4);
        host.Split("__cache.TryLead(__cacheKey").Length.Should().Be(4);
        host.Should().Contain(rawReturn);

        var hit = Case(host, "Hit");
        hit.IndexOf("__cache.TryGet(__cacheKey").Should().BeLessThan(hit.IndexOf("__Req"), "sin processors se consulta antes de deserializar");
        hit.IndexOf(".Hit(").Should().BeLessThan(hit.IndexOf("__cache.Complete("));

        var shout = Case(host, "Shout");
        shout.IndexOf(".Process(").Should().BeLessThan(shout.IndexOf("__cache.TryGet("), "un acierto no se salta los param processors");

        Case(host, "Plain").Should().NotContain("__cache");
    }

    [Fact]
    public void IgnoredCachesWarn()
    {
        var r = GeneratorHarness.Run(Source.Replace("CONN", "Tcp"));

        r.Errors.Should().BeEmpty();
        var ignored = r.All.Where(d => d.Id == "SCLSL017").Select(d => d.GetMessage()).ToList();
        ignored.Should().HaveCount(3);
        ignored.Should().Contain(m => m.Contains("'Ping'") && m.Contains("command"))
            .And.Contain(m => m.Contains("'TryGet'") && m.Contains("out/ref"))
            .And.Contain(m => m.Contains("'Zero'") && m.Contains("positive"));
    }
}
