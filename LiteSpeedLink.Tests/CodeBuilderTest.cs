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

    [Fact]
    public void Line_offset_applies_to_that_line_only()
    {
        var code = new CodeBuilder(1);
        code.Line("Run(").Line("a,", 1).Append("b").Append(",\nc").Line("done:", -1).Line("x();").Line("y", -5);

        code.ToString().Should().Be("    Run(\n        a,b,\n    c\ndone:\n    x();\ny");
        code.Depth.Should().Be(1);
    }

    [Fact]
    public void PrependLine_breaks_exactly_n_times_like_appending_newlines()
    {
        var code = new CodeBuilder(1);
        code.Append("a;").PrependLine(2, "b;").PrependLine(2, "c;").PrependLine(1);

        code.ToString().Should().Be(new CodeBuilder(1).Append("a;\n\nb;\n\nc;\n").ToString())
            .And.Be("    a;\n\n    b;\n\n    c;\n");
    }

    [Fact]
    public void Parens_wraps_only_when_asked()
    {
        var code = new CodeBuilder();
        code.Parens(() => code.Append("a, b")).Append(' ').Parens(() => code.Append("c"), wrap: false).Parens(null);

        code.ToString().Should().Be("(a, b) c()");
    }
}
