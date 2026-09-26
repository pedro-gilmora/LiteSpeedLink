using MemoryPack;

namespace LiteSpeedLink.Benchmarks;

[MemoryPackable]
public partial record struct Credentials(string User, string Password);

public static class Payloads
{
    public const long OpId = -2153953080512997722;

    public static readonly Credentials Small = new("pedro", "s3cr3t");

    /// <summary>~4 KB: por encima del umbral seguro de stackalloc.</summary>
    public static readonly string Medium = new('x', 4 * 1024);

    /// <summary>~256 KB: el caso que provocaba StackOverflow con el stackalloc no acotado.</summary>
    public static readonly string Large = new('x', 256 * 1024);
}
