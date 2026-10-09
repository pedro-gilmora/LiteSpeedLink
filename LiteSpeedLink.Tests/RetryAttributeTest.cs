using FluentAssertions;

namespace LiteSpeedLink.Tests;

/// <summary>[ClientRetry]/[ServerRetry]: bucle inline alrededor de la llamada (transporte/handler), sin structs ni delegados por operacion.</summary>
public class RetryAttributeTest
{
    const string Prelude = """
        using SourceCrafter.DependencyInjection.Attributes;
        using SourceCrafter.LiteSpeedLink;

        namespace Probe;

        public sealed class Trim : IPipeline<string>
        {
            public (ResponseStatus, string) Process(string input) => (ResponseStatus.Success, input.Trim());
        }

        """;

    const string ClientSource = Prelude + """
        public interface IContract : IServiceUnit
        {
            [ClientRetry(3, 50)] int Hit(int n);
            [ClientRetry(4)] System.Threading.Tasks.Task<string> EchoAsync(string s);
            [ClientRetry] void Ping();
            [ClientRetry(2)] bool TryGet(int key, out string value);
            [ClientRetry(3)] string Shout([ClientProcessor<Trim>] string s);
            [ServerRetry(5)] int ServerOnly(int n);
            int Plain(int n);
        }

        [ServiceClient]
        [ServiceUnit<IContract>]
        [ServiceProvider]
        [Singleton<Trim>]
        public sealed partial class Client;
        """;

    const string HostSource = Prelude + """
        public interface IContract : IServiceUnit
        {
            [ServerRetry(3, 50)] int Hit(int n);
            [ServerRetry(4)] System.Threading.Tasks.Task<string> EchoAsync(string s);
            [ServerRetry(2)] bool TryGet(int key, out string value);
            [ServerRetry(5)] string Shout([ServerProcessor<Trim>] string s);
            [ClientRetry(6)] int ClientOnly(int n);
            int Plain(int n);
        }

        public sealed class Impl : IContract
        {
            public int Hit(int n) => n;
            public System.Threading.Tasks.Task<string> EchoAsync(string s) => System.Threading.Tasks.Task.FromResult(s);
            public bool TryGet(int key, out string value) { value = ""; return true; }
            public string Shout(string s) => s;
            public int ClientOnly(int n) => n;
            public int Plain(int n) => n;
        }

        [ServiceHost]
        [ServiceProvider]
        [Singleton<Trim>]
        [Scoped<IContract, Impl>]
        public partial class Host;
        """;

    static int Loops(string code) => code.Split("for (var __attempt = 1; ; __attempt++)").Length - 1;

    [Fact]
    public void ClientEmitsInlineLoopWithoutPerOperationStructs()
    {
        var r = GeneratorHarness.Run(ClientSource);

        r.Errors.Should().BeEmpty();
        r.HasDiagnostic("SCLSL015").Should().BeFalse();
        var client = r.Sources.Values.Single(s => s.Contains("class ContractClient("));

        client.Should().NotContain("struct __Call").And.NotContain("WrappedCall");
        client.Should().Contain("__attempt < 3").And.Contain("__attempt < 4").And.Contain("__attempt < 2");
        client.Should().NotContain("__attempt < 5", "[ServerRetry] no afecta al cliente");
        client.Should().Contain("Task.Delay(50, ").And.Contain("Thread.Sleep(50)");
        client.Should().Contain("catch (global::System.TimeoutException)");
        // Un bucle por variante generada (async + sync, salvo EchoAsync); Plain y ServerOnly no llevan.
        Loops(client).Should().Be(9);
        // Processors fuera del bucle: Trim corre una vez antes del for.
        var shout = client[client.IndexOf("ShoutAsync(")..];
        shout.IndexOf(".Process(").Should().BeLessThan(shout.IndexOf("for (var __attempt"));
    }

    [Fact]
    public void HostRetriesOnlyTheHandlerCall()
    {
        var r = GeneratorHarness.Run(HostSource);

        r.Errors.Should().BeEmpty();
        r.HasDiagnostic("SCLSL015").Should().BeFalse();
        var host = r.Sources.Single(s => s.Key.Contains(".host")).Value;

        host.Should().Contain("__attempt < 3").And.Contain("__attempt < 4").And.Contain("__attempt < 2").And.Contain("__attempt < 5");
        host.Should().NotContain("__attempt < 6", "[ClientRetry] no afecta al host");
        host.Should().Contain("catch (global::System.TimeoutException) when (__attempt < 3 && !__token.IsCancellationRequested)");
        Loops(host).Should().Be(4);
        // out declarado fuera del bucle para que la respuesta lo vea.
        host.Should().Contain("string value;").And.Contain("out value");
        // Server processors fuera del bucle.
        var shout = host[host.IndexOf("IContract.Shout(")..];
        shout.IndexOf(".Process(").Should().BeLessThan(shout.IndexOf("for (var __attempt"));
    }

    [Theory]
    [InlineData(ClientSource)]
    [InlineData(HostSource)]
    public void RetryOnBothSidesWarnsAboutLatency(string source)
    {
        var r = GeneratorHarness.Run(source.Replace("int Plain(int n);", "[ClientRetry(3)][ServerRetry(4)] int Plain(int n);"));

        r.Errors.Should().BeEmpty();
        r.All.Should().ContainSingle(d => d.Id == "SCLSL015")
            .Which.GetMessage().Should().Contain("Plain").And.Contain("12");
    }

    [Theory]
    [InlineData("[ClientRetry(intervalMs: 7, attempts: 5)]", "__attempt < 5", "Sleep(7)")]
    [InlineData("[ClientRetry(5, intervalMs: 7)]", "__attempt < 5", "Sleep(7)")]
    [InlineData("[ClientRetry(intervalMs: 7)]", "__attempt < 3", "Sleep(7)")]
    public void NamedArgumentsResolveByParameterName(string attr, string attempts, string pause)
    {
        var r = GeneratorHarness.Run(ClientSource.Replace("int Plain(int n);", attr + " int Plain(int n);"));

        r.Errors.Should().BeEmpty();
        var client = r.Sources.Values.Single(s => s.Contains("class ContractClient("));
        var plain = client[client.IndexOf("PlainAsync(")..];

        plain.Should().Contain(attempts).And.Contain(pause);
    }

    [Theory]
    [InlineData(ClientSource)]
    [InlineData(HostSource)]
    public void RetryOnStreamIsIgnoredWithWarning(string source)
    {
        var r = GeneratorHarness.Run(source.Replace("int Plain(int n);",
            "int Plain(int n); [ClientRetry(7)][ServerRetry(8)] System.Collections.Generic.IAsyncEnumerable<int> Ticks(int n);")
            .Replace("public int Plain(int n) => n;", "public int Plain(int n) => n; public async System.Collections.Generic.IAsyncEnumerable<int> Ticks(int n) { yield return n; }"));

        r.Errors.Should().BeEmpty();
        r.All.Should().ContainSingle(d => d.Id == "SCLSL016").Which.GetMessage().Should().Contain("Ticks");
        r.HasDiagnostic("SCLSL015").Should().BeFalse();
        r.Sources.Values.Should().NotContain(s => s.Contains("__attempt < 7") || s.Contains("__attempt < 8"));
    }
}
