using FluentAssertions;

namespace LiteSpeedLink.Tests;

/// <summary>Solo los contratos <c>IServiceUnit</c> se exponen como RPC; las dependencias concretas no.</summary>
public class ServiceUnitExportTest
{
    [Fact]
    public void ConcreteSingletonIsNotExported()
    {
        var r = GeneratorHarness.Run("""
            using SourceCrafter.DependencyInjection.Attributes;
            using SourceCrafter.LiteSpeedLink;

            namespace Probe;

            public interface IContract : IServiceUnit { int Op(int value); }

            public sealed class Store
            {
                public int Next(int id) => id + 1;
                void Hidden() { }
            }

            public sealed class Impl(Store store) : IContract { public int Op(int value) => store.Next(value); }

            [ServiceHost]
            [ServiceProvider]
            [Singleton<Store>]
            [Scoped<IContract, Impl>]
            public partial class Host;
            """);

        r.Errors.Should().BeEmpty();
        var host = r.Sources.Single(s => s.Key.Contains(".host")).Value;
        host.Should().Contain("IContract.Op").And.NotContain("Store.Next").And.NotContain("Store.Hidden");
    }
}
