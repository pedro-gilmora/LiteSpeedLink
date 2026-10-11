using FluentAssertions;
using SourceCrafter.DependencyInjection.Generation;

namespace LiteSpeedLink.Tests;

public class CodeBuilderTest
{
    [Fact]
    public void Blocks_indent_on_append_and_deindent_on_close()
    {
        var inner = new CodeBuilder();
        inner.Open("if (x)").Line("a();\n\nb();").Close();

        var code = new CodeBuilder();
        code.Line("namespace N;").Blank()
            .Open("class C")
                .Open("void M()")
                    .Line("var f = static () => ").OpenInline().Append("return 1;").Close(";")
                    .Line("Run(() =>").Open().Append(inner).Close(", token);")
                .Close()
            .Close();

        code.ToString().Should().Be("""
            namespace N;

            class C
            {
                void M()
                {
                    var f = static () => { return 1; };
                    Run(() =>
                    {
                        if (x)
                        {
                            a();

                            b();
                        }
                    }, token);
                }
            }
            """.ReplaceLineEndings("\n"));
        code.Depth.Should().Be(0);
    }

    [Fact]
    public void Inline_block_broken_by_lines_closes_on_its_own_line()
    {
        var code = new CodeBuilder(1);
        code.Append("try ").OpenInline().Line("x();").Close().Append(" finally ").OpenInline().Append("y();").Close();

        code.ToString().Should().Be("    try {\n        x();\n    } finally { y(); }");
    }

    [Fact]
    public void Line_reuses_a_whitespace_only_line_at_current_depth()
    {
        var code = new CodeBuilder();
        code.Append("{\n            ").Indent().Line("x();");

        code.ToString().Should().Be("{\n    x();");
    }
}
