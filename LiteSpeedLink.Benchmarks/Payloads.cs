using MemoryPack;

namespace LiteSpeedLink.Benchmarks;

[MemoryPackable]
public partial record struct Credentials(string User, string Password);

public static class Payloads
{
    /// <summary>opId de 32 bits (lo que viaja en <see cref="SourceCrafter.LiteSpeedLink.Framing.OpIdSize"/> = 4 bytes).</summary>
    public const long OpId = -1946214490;

    public static readonly Credentials Small = new("pedro", "s3cr3t");

    /// <summary>~4 KB: por encima del umbral seguro de stackalloc.</summary>
    public static readonly string Medium = new('x', 4 * 1024);

    /// <summary>~256 KB: el caso que provocaba StackOverflow con el stackalloc no acotado.</summary>
    public static readonly string Large = new('x', 256 * 1024);
}
