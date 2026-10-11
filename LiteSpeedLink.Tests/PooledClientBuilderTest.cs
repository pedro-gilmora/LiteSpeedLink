using FluentAssertions;

namespace LiteSpeedLink.Tests;

/// <summary>Los metodos ValueTask del cliente generado reutilizan su caja async; los contratos Task siguen con la normal.</summary>
public class PooledClientBuilderTest
{
    [Fact]
    public void ValueTaskMethodsUsePooledBuilder()
    {
        var r = GeneratorHarness.Run("""
            using SourceCrafter.DependencyInjection.Attributes;
            using SourceCrafter.LiteSpeedLink;

            namespace Probe;

            public interface IContract : IServiceUnit
            {
                int Plain(int n);

                ValueTask<int> PlainValueAsync(int n);

                Task<int> PlainTaskAsync(int n);

                void Fire(int n);
            }

            [ServiceClient]
            [ServiceUnit<IContract>]
            [ServiceProvider]
            public sealed partial class Client;
            """);

        r.Errors.Should().BeEmpty();
        var client = System.Text.RegularExpressions.Regex.Replace(r.Sources.Values.Single(s => s.Contains("PlainTaskAsync(")), @"\s+", " ");
        // Solo GetRawAsync: su ValueTask sale tal cual, sin async ni caja.
        const string direct = "public global::System.Threading.Tasks.ValueTask<int> ";

        client.Should()
            .Contain(direct + "PlainAsync(int n, global::System.Threading.CancellationToken @__token = default) { return __connection.GetRawAsync(")
            .And.Contain(direct + "PlainValueAsync(int n, global::System.Threading.CancellationToken @__token = default) { return __connection.GetRawAsync(")
            .And.NotContain("PoolingAsyncValueTaskMethodBuilder<>")
            .And.Contain("PoolingAsyncValueTaskMethodBuilder))] public async global::System.Threading.Tasks.ValueTask FireAsync(")
            .And.Contain("public async global::System.Threading.Tasks.Task<int> PlainTaskAsync(")
            .And.NotContain("PoolingAsyncValueTaskMethodBuilder<>))] public async global::System.Threading.Tasks.Task<int> PlainTaskAsync(");
    }
}
