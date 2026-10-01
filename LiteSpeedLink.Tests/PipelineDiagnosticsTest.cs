using FluentAssertions;

namespace LiteSpeedLink.Tests;

/// <summary>Diagnosticos de procesadores (SCLSL010-014) sobre el generador real, en memoria.</summary>
public class PipelineDiagnosticsTest
{
    const string Prelude = """
        using LiteSpeedLink.Abstractions.Internals;
        using SourceCrafter.DependencyInjection.Attributes;
        using SourceCrafter.LiteSpeedLink;

        namespace Probe;

        public sealed class Trim : IPipeline<string>
        {
            public (ResponseStatus, string) Process(string input) => (ResponseStatus.Success, input.Trim());
        }

        public sealed class ParseInt : IPipeline<string, int>
        {
            public (ResponseStatus, int) Process(string input) => (ResponseStatus.Success, int.Parse(input));
        }

        public sealed class NotPipe;

        public sealed class Explicit : IPipeline<string>
        {
            (ResponseStatus, string) IPipeline<string>.Process(string input) => (ResponseStatus.Success, input);
        }

        public sealed class Impl : IContract
        {
            public string Op(string value) => value;
        }

        [ServiceHost]
        [ServiceProvider]
        [Singleton<Trim>]
        [Singleton<ParseInt>]
        [Singleton<NotPipe>]
        [Singleton<Explicit>]
        [Scoped<IContract, Impl>]
        public partial class Host;

        """;

    static GeneratorHarness.Result Run(string contract) => GeneratorHarness.Run(Prelude + contract);

    [Fact]
    public void ValidPipelineCompilesClean()
    {
        var r = Run("public interface IContract : IServiceUnit { string Op([ServerPreProcessor<Trim>] string value); }");

        r.Errors.Should().BeEmpty();
        r.Sources.Keys.Should().Contain(k => k.Contains(".host"), "el parcial de LiteSpeedLink debe haberse cargado");
    }

    [Theory]
    [InlineData("SCLSL010", "string Op([ServerPreProcessor<NotPipe>] string value);")]
    [InlineData("SCLSL011", "string Op([ServerPreProcessor<ParseInt>] string value);")]
    [InlineData("SCLSL012", "void Op([ServerPreProcessor<Trim>] ref string value);")]
    [InlineData("SCLSL013", "string Op([ServerPreProcessor<Upper>] string value); } public sealed class Upper : IPipeline<string> { public (ResponseStatus, string) Process(string i) => (ResponseStatus.Success, i);")]
    [InlineData("SCLSL014", "string Op([ServerPreProcessor<Explicit>] string value);")]
    public void ReportsDiagnostic(string id, string member)
    {
        Run("public interface IContract : IServiceUnit { " + member + " }").HasDiagnostic(id).Should().BeTrue();
    }
}
