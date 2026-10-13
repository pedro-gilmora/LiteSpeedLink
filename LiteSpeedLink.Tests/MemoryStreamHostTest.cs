using FluentAssertions;

namespace LiteSpeedLink.Tests;

/// <summary>El host de memoria emite los streams con <c>Yield</c>, no los serializa como valor.</summary>
public class MemoryStreamHostTest
{
    [Fact]
    public void StreamsUseYield()
    {
        var r = GeneratorHarness.Run("""
            using SourceCrafter.DependencyInjection.Attributes;
            using SourceCrafter.LiteSpeedLink;

            namespace Probe;

            public interface IContract : IServiceUnit
            {
                [Stream(Batch = 0)]
                IAsyncEnumerable<int> Live(int count, CancellationToken token);

                IEnumerable<int> Range(int count);
            }

            public sealed class Impl : IContract
            {
                public async IAsyncEnumerable<int> Live(int count, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
                {
                    for (var i = 0; i < count; i++) { await Task.Yield(); yield return i; }
                }

                public IEnumerable<int> Range(int count)
                {
                    for (var i = 0; i < count; i++) yield return i;
                }
            }

            [ServiceHost]
            [ServiceProvider]
            [Scoped<IContract, Impl>]
            public partial class Host;
            """);

        r.Errors.Should().BeEmpty();
        var host = r.Sources.Single(s => s.Key.Contains(".host")).Value;
        // Handler async (pooled) tambien en Memory: el stream async se espera, no bloquea un hilo del pool.
        host.Should().Contain("return await __context.Yield(___result).ConfigureAwait(false);")
            .And.Contain("return __context.Yield(___result);")
            .And.Contain("StartMemoryServerAsync(")
            .And.Contain("PoolingAsyncValueTaskMethodBuilder<>")
            .And.Contain("private async global::System.Threading.Tasks.ValueTask<global::SourceCrafter.LiteSpeedLink.ResponseStatus> __Live_")
            .And.Contain("private global::SourceCrafter.LiteSpeedLink.ResponseStatus __Range_")
            .And.NotContain("__context.Return(___result)")
            .And.NotContain(".GetAwaiter().GetResult();\n");
    }

    [Theory]
    [InlineData("int Echo(int v);", "public int Echo(int v) => v;", "IPipeline<int>", "public (ResponseStatus, int) Process(int v) => (ResponseStatus.Success, v);", false)]
    [InlineData("ValueTask<int> Echo(int v);", "public ValueTask<int> Echo(int v) => new(v);", "IPipeline<int>", "public (ResponseStatus, int) Process(int v) => (ResponseStatus.Success, v);", true)]
    [InlineData("int Echo(int v);", "public int Echo(int v) => v;", "IAsyncValuePipeline<int>", "public ValueTask<(ResponseStatus, int)> ProcessAsync(int v) => new((ResponseStatus.Success, v));", true)]
    public void HandlerIsAsyncOnlyIfSomethingAwaits(string op, string impl, string pipe, string process, bool isAsync)
    {
        var r = GeneratorHarness.Run($$"""
            using SourceCrafter.DependencyInjection.Attributes;
            using SourceCrafter.LiteSpeedLink;

            namespace Probe;

            public interface IContract : IServiceUnit
            {
                [return: ServerProcessor<Pipe>]
                {{op}}

                int Plain(int v);
            }

            public sealed class Impl : IContract
            {
                {{impl}}
                public int Plain(int v) => v;
            }

            public sealed class Pipe : {{pipe}}
            {
                {{process}}
            }

            [ServiceHost]
            [ServiceProvider]
            [Scoped<IContract, Impl>]
            [Singleton<Pipe>]
            public partial class Host;
            """);

        r.Errors.Should().BeEmpty();
        var host = r.Sources.Single(s => s.Key.Contains(".host")).Value;

        if (isAsync)
            host.Should().Contain("StartMemoryServerAsync(")
                .And.Contain("private async global::System.Threading.Tasks.ValueTask<global::SourceCrafter.LiteSpeedLink.ResponseStatus> __Echo_")
                .And.Contain("private global::SourceCrafter.LiteSpeedLink.ResponseStatus __Plain_")
                .And.Contain("=> new(__Plain_")
                .And.Contain("private global::System.Threading.Tasks.ValueTask<global::SourceCrafter.LiteSpeedLink.ResponseStatus> HandleRequestsAsync(long id, ");
        else
            host.Should().Contain("StartMemoryServer(").And.NotContain("StartMemoryServerAsync(")
                .And.Contain("private global::SourceCrafter.LiteSpeedLink.ResponseStatus HandleRequestsAsync(")
                .And.NotContain("await ").And.NotContain("PoolingAsyncValueTaskMethodBuilder");
    }

    [Fact]
    public void IdleTimeoutFromContractReachesMemoryClient()
    {
        var r = GeneratorHarness.Run("""
            using SourceCrafter.DependencyInjection.Attributes;
            using SourceCrafter.LiteSpeedLink;

            namespace Probe;

            public interface IContract : IServiceUnit
            {
                [Stream(IdleTimeoutMs = -1)]
                IAsyncEnumerable<int> Live(string room, CancellationToken token);
            }

            [ServiceClient]
            [ServiceUnit<IContract>]
            [ServiceProvider]
            public sealed partial class Client;
            """);

        r.Errors.Should().BeEmpty();
        r.Sources.Values.Should().Contain(s => s.Contains("EnumerateAsync<string, int>(") && s.Contains("room, -1, token)"));
    }
}
