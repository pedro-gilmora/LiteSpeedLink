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
        host.Should().Contain("__context.Yield(___result).GetAwaiter().GetResult();")
            .And.Contain("__context.Yield(___result);")
            .And.NotContain("__context.Return(___result)");
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
