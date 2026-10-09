using FluentAssertions;

namespace LiteSpeedLink.Tests;

/// <summary>Diagnosticos de procesadores (SCLSL010-014) sobre el generador real, en memoria.</summary>
public class PipelineDiagnosticsTest
{
    const string Prelude = """
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

        [ServiceHost]
        [ServiceProvider]
        [Singleton<Trim>]
        [Singleton<ParseInt>]
        [Singleton<NotPipe>]
        [Singleton<Explicit>]
        [Scoped<IContract, Impl>]
        public partial class Host;

        """;

    static GeneratorHarness.Result Run(string contract, string impl = "public string Op(string value) => value;") =>
        GeneratorHarness.Run(Prelude + "public sealed class Impl : IContract { " + impl + " }\n" + contract);

    [Theory]
    [InlineData("string Op([ServerProcessor<Trim>] string value);", "public string Op(string value) => value;")]
    [InlineData("void Op([ServerProcessor<Trim>] ref string value, out int length);", "public void Op(ref string value, out int length) => length = value.Length;")]
    public void ValidPipelineCompilesClean(string member, string impl)
    {
        var r = Run("public interface IContract : IServiceUnit { " + member + " }", impl);

        r.Errors.Should().BeEmpty();
        r.Sources.Keys.Should().Contain(k => k.Contains(".host"), "el parcial de LiteSpeedLink debe haberse cargado");
    }

    [Theory]
    [InlineData("SCLSL010", "string Op([ServerProcessor<NotPipe>] string value);")]
    [InlineData("SCLSL011", "string Op([ServerProcessor<ParseInt>] string value);")]
    [InlineData("SCLSL012", "void Op([ServerProcessor<Trim>] out string value);")]
    [InlineData("SCLSL012", "void Op([ServerProcessor<ParseInt>] ref string value);")]
    [InlineData("SCLSL013", "string Op([ServerProcessor<Upper>] string value); } public sealed class Upper : IPipeline<string> { public (ResponseStatus, string) Process(string i) => (ResponseStatus.Success, i);")]
    [InlineData("SCLSL014", "string Op([ServerProcessor<Explicit>] string value);")]
    public void ReportsDiagnostic(string id, string member)
    {
        Run("public interface IContract : IServiceUnit { " + member + " }").HasDiagnostic(id).Should().BeTrue();
    }
}
