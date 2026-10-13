using FluentAssertions;

namespace LiteSpeedLink.Tests;

/// <summary>[ClientMiddleware]/[ServerMiddleware]: lambdas static anidadas alrededor de la llamada, sin structs por capa.</summary>
public class MiddlewareGeneratorTest
{
    const string Prelude = """
        using SourceCrafter.DependencyInjection.Attributes;
        using SourceCrafter.LiteSpeedLink;

        namespace Probe;

        public sealed class Trim : IPipeline<string>
        {
            public (ResponseStatus, string) Process(string input) => (ResponseStatus.Success, input.Trim());
        }

        public sealed class Log : IMiddleware, IAsyncMiddleware
        {
            public TRes Invoke<TState, TRes>(TState state, Func<TState, TRes> next) => next(state);
            public ValueTask<TRes> InvokeAsync<TState, TRes>(TState state, Func<TState, CancellationToken, ValueTask<TRes>> next, CancellationToken token) => next(state, token);
        }

        public sealed class Timing : IMiddleware, IAsyncMiddleware
        {
            public TRes Invoke<TState, TRes>(TState state, Func<TState, TRes> next) => next(state);
            public ValueTask<TRes> InvokeAsync<TState, TRes>(TState state, Func<TState, CancellationToken, ValueTask<TRes>> next, CancellationToken token) => next(state, token);
        }

        public sealed class AsyncOnly : IAsyncMiddleware
        {
            public ValueTask<TRes> InvokeAsync<TState, TRes>(TState state, Func<TState, CancellationToken, ValueTask<TRes>> next, CancellationToken token) => next(state, token);
        }

        """;

    const string Contract = """
        public interface IContract : IServiceUnit
        {
            [ClientMiddleware<Log>, ServerMiddleware<Log>] int Hit(int n);
            [ClientMiddleware<Log>, ClientMiddleware<Timing>, ServerMiddleware<Log>, ServerMiddleware<Timing>, ClientRetry(3, 5), ServerRetry(2)] Task<string> EchoAsync(string s, int k);
            [ClientMiddleware<Log>, ServerMiddleware<Log>] void Ping();
            [ClientMiddleware<Log>, ServerMiddleware<Log>] bool TryGet(int key, out string value);
            [ClientMiddleware<Timing>, ServerMiddleware<Timing>] string Shout([ClientProcessor<Trim>, ServerProcessor<Trim>] string s);
            int Plain(int n);
            [ServerMiddleware<Log>] int Injected(int n, [Singleton] Trim trim);
        }
        """;

    const string ClientSource = Prelude + Contract + """

        [ServiceClient]
        [ServiceUnit<IContract>]
        [ServiceProvider]
        [Singleton<Trim>, Singleton<Log>, Singleton<Timing>, Singleton<AsyncOnly>]
        public sealed partial class Client;
        """;

    const string HostSource = Prelude + Contract + """

        public sealed class Impl : IContract
        {
            public int Hit(int n) => n;
            public Task<string> EchoAsync(string s, int k) => Task.FromResult(s);
            public void Ping() { }
            public bool TryGet(int key, out string value) { value = ""; return true; }
            public string Shout(string s) => s;
            public int Plain(int n) => n;
            public int Injected(int n, Trim trim) => n;
        }

        [ServiceHost]
        [ServiceProvider]
        [Singleton<Trim>, Singleton<Log>, Singleton<Timing>, Singleton<AsyncOnly>]
        [Scoped<IContract, Impl>]
        public partial class Host;
        """;

    [Fact]
    public void ClientWrapsTransportCallWithStaticLambdas()
    {
        var r = GeneratorHarness.Run(ClientSource);

        r.Errors.Should().BeEmpty();
        var client = r.Sources.Values.Single(s => s.Contains("class ContractClient("));

        client.Should().NotContain("struct __").And.Contain(".InvokeAsync(").And.Contain(".Invoke(").And.Contain("static ");
        // Retry dentro de las capas; processors fuera (antes del primer middleware).
        var echo = client[client.IndexOf("EchoAsync(")..];
        echo.IndexOf(".InvokeAsync(").Should().BeLessThan(echo.IndexOf("for (var __attempt"));
        var shout = client[client.IndexOf("ShoutAsync(")..];
        shout.IndexOf(".Process(").Should().BeLessThan(shout.IndexOf(".InvokeAsync("));
        var plain = client[client.IndexOf("PlainAsync(")..];
        plain[..plain.IndexOf("\n    }")].Should().NotContain(".InvokeAsync(");
    }

    [Fact]
    public void HostWrapsHandlerCallWithStaticLambdas()
    {
        var r = GeneratorHarness.Run(HostSource);

        r.Errors.Should().BeEmpty();
        var host = r.Sources.Single(s => s.Key.Contains(".host")).Value;

        host.Should().NotContain("struct __").And.Contain(".InvokeAsync(");
        var shout = host[host.IndexOf(" __Shout_")..];
        shout.IndexOf(".Process(").Should().BeLessThan(shout.IndexOf(".Invoke"));
    }

    [Fact]
    public void SyncPathRequiresSyncMiddleware()
    {
        var r = GeneratorHarness.Run(ClientSource.Replace("[ClientMiddleware<Log>, ServerMiddleware<Log>] int Hit", "[ClientMiddleware<AsyncOnly>] int Hit"));

        r.HasDiagnostic("SCLSL020").Should().BeTrue();
    }

    [Theory]
    [InlineData(ClientSource)]
    [InlineData(HostSource)]
    public void MiddlewareOnStreamIsIgnoredWithWarning(string source)
    {
        var r = GeneratorHarness.Run(source.Replace("int Plain(int n);",
            "int Plain(int n); [ClientMiddleware<Log>, ServerMiddleware<Log>] System.Collections.Generic.IAsyncEnumerable<int> Ticks(int n);")
            .Replace("public int Plain(int n) => n;", "public int Plain(int n) => n; public async System.Collections.Generic.IAsyncEnumerable<int> Ticks(int n) { yield return n; }"));

        r.Errors.Should().BeEmpty();
        r.All.Should().ContainSingle(d => d.Id == "SCLSL021");
    }
}
