using FluentAssertions;

namespace LiteSpeedLink.Tests;

/// <summary>[ClientCache]/[ServerCache] + [CacheKey]: forma de la clave decidida en compilacion, SCLSL017/018/019.</summary>
public class CacheKeyGeneratorTest
{
    const string Types = """
        using SourceCrafter.DependencyInjection.Attributes;
        using SourceCrafter.LiteSpeedLink;

        namespace Probe;

        public sealed class Customer { public string Region { get; set; } = ""; }
        public sealed class OrderQuery { public int Id { get; set; } public Customer Customer { get; set; } = new(); }
        public sealed record Point(int X, int Y);
        public sealed record Bag(int[] Items);

        public sealed class Trim : IPipeline<string>
        {
            public (ResponseStatus, string) Process(string input) => (ResponseStatus.Success, input.Trim());
        }
        """;

    const string Contract = """
        public interface IContract : IServiceUnit
        {
            [ClientCache(5000)] int One(int n);
            [ClientCache(5000)] System.Threading.Tasks.Task<string> TwoAsync(int a, string b, System.Threading.CancellationToken token);
            [ClientCache(5000)] int Rec(Point p);
            [ClientCache(5000)] int Bytes(OrderQuery q);
            [ClientCache(5000)] string Sub([CacheKey] string sku, System.Guid traceId);
            [ClientCache(5000)] string Members([CacheKey(nameof(OrderQuery.Id), nameof(OrderQuery.Customer.Region))] OrderQuery query);
            [ClientCache(5000), ServerCache(5000)]
            [CacheKey(nameof(query.Id), nameof(query.Customer.Region), nameof(currency))]
            string Rooted(OrderQuery query, string currency, System.Guid traceId);
            [ClientCache(5000)] string Literal([CacheKey("Customer.Region")] OrderQuery query);
            [ClientCache(5000)] string Processed([ClientProcessor<Trim>] string s);
            [ClientCache(5000)] int None();
        }
        """;

    static string Source(string side) => Types + Contract + (side == "client"
        ? """

        [ServiceClient(ServiceConnectionType.Tcp)]
        [ServiceUnit<IContract>]
        [ServiceProvider]
        [Singleton<Trim>]
        public sealed partial class Client;
        """
        : """

        public sealed class Impl : IContract
        {
            public int One(int n) => n;
            public System.Threading.Tasks.Task<string> TwoAsync(int a, string b, System.Threading.CancellationToken token) => System.Threading.Tasks.Task.FromResult(b);
            public int Rec(Point p) => p.X;
            public int Bytes(OrderQuery q) => q.Id;
            public string Sub(string sku, System.Guid traceId) => sku;
            public string Members(OrderQuery query) => "";
            public string Rooted(OrderQuery query, string currency, System.Guid traceId) => currency;
            public string Literal(OrderQuery query) => "";
            public string Processed(string s) => s;
            public int None() => 0;
        }

        [ServiceHost(ServiceConnectionType.Tcp)]
        [ServiceProvider]
        [Singleton<Trim>]
        [Scoped<IContract, Impl>]
        public partial class Host;
        """);

    static string Method(string client, string name)
    {
        var start = client.IndexOf(" " + name + "Async(");
        var end = client.IndexOf("\n    }", start);
        return client[start..end];
    }

    [Fact]
    public void ClientKeyShapeIsDecidedAtCompileTime()
    {
        var r = GeneratorHarness.Run(Source("client"));

        r.Errors.Should().BeEmpty();
        var client = r.Sources.Single(s => s.Key.Contains("Contract.client")).Value;

        // Una hoja de tipo valor: su tipo tal cual; varias: ValueTuple; el token nunca entra.
        client.Should().Contain("CacheManager<int, int>(5000, 1024)")
            .And.Contain("CacheManager<(int, string), string>")
            .And.Contain("CacheManager<global::System.ValueTuple<global::Probe.Point>, int>");
        Method(client, "Two").Should().Contain("var __cacheKey = (a, b);");

        // Sin igualdad por valor: bytes con comparador por contenido, y esos mismos bytes son la peticion.
        var bytes = Method(client, "Bytes");
        bytes.Should().Contain("var __cacheKey = __Req").And.Contain("GetRawAsync(").And.Contain(", __cacheKey, ");
        client.Should().Contain("ByteContentComparer.Instance");

        // [CacheKey]: subconjunto, rutas nameof (parametro y metodo) y literales con ?. en tramos de referencia.
        Method(client, "Sub").Should().Contain("new global::System.ValueTuple<string>(sku)");
        Method(client, "Members").Should().Contain("(query?.Id, query?.Customer?.Region)");
        Method(client, "Rooted").Should().Contain("(query?.Id, query?.Customer?.Region, currency)");
        Method(client, "Literal").Should().Contain("query?.Customer?.Region");

        // Con processors la clave son los argumentos originales y la consulta va antes de procesarlos y del retry.
        var processed = Method(client, "Processed");
        processed.IndexOf("__cache.TryGet(").Should().BeLessThan(processed.IndexOf(".Process("));
        processed.Should().Contain("__cache.Complete(__cacheKey, __flight, __r);");

        Method(client, "None").Should().Contain("var __cacheKey = (byte)0;");

        r.All.Where(d => d.Id == "SCLSL018").Select(d => d.GetMessage()).Should().ContainSingle(m => m.Contains("'Bytes'") && m.Contains("'q'"));
    }

    [Fact]
    public void ServerUsesTypedKeyOnlyWithCacheKey()
    {
        var r = GeneratorHarness.Run(Source("host"));

        r.Errors.Should().BeEmpty();
        var host = r.Sources.Single(s => s.Key.Contains(".host")).Value;

        host.Should().Contain("CacheManager<(int?, string?, string), global::System.ReadOnlyMemory<byte>>(5000, 1024)")
            .And.Contain("var __cacheKey = (query?.Id, query?.Customer?.Region, currency);");
        // SCLSL018 es del cliente: el servidor sin [CacheKey] ya usa los bytes de la peticion.
        r.HasDiagnostic("SCLSL018").Should().BeFalse();
    }

    [Theory]
    [InlineData("[ClientCache(5000)] int M([CacheKey(\"Nope\")] OrderQuery q);", "'Nope' not found")]
    [InlineData("[ClientCache(5000)] int M([CacheKey] OrderQuery q);", "no value equality")]
    [InlineData("[ClientCache(5000)] int M([CacheKey] Bag b);", "no value equality")]
    [InlineData("[ClientCache(5000), CacheKey(nameof(a))] int M([CacheKey] int a);", "at once")]
    public void InvalidKeysFail(string member, string error)
    {
        var r = GeneratorHarness.Run(Types + "public interface IContract : IServiceUnit { " + member + """
             }

            [ServiceClient(ServiceConnectionType.Tcp)]
            [ServiceUnit<IContract>]
            [ServiceProvider]
            public sealed partial class Client;
            """);

        r.All.Where(d => d.Id == "SCLSL019").Select(d => d.GetMessage()).Should().ContainSingle(m => m.Contains(error));
    }

    [Fact]
    public void CacheKeyWithoutCacheWarns()
    {
        var r = GeneratorHarness.Run(Types + """
            public interface IContract : IServiceUnit
            {
                int M([CacheKey] int a);
                [ClientCache(5000)] int T(int a, [CacheKey] System.Threading.CancellationToken token);
            }

            [ServiceClient(ServiceConnectionType.Tcp)]
            [ServiceUnit<IContract>]
            [ServiceProvider]
            public sealed partial class Client;
            """);

        var ignored = r.All.Where(d => d.Id == "SCLSL017").Select(d => d.GetMessage()).ToList();
        ignored.Should().Contain(m => m.Contains("'M'") && m.Contains("no [ClientCache]"))
            .And.Contain(m => m.Contains("'T'") && m.Contains("CancellationToken"));
    }
}
