using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace LiteSpeedLink.Benchmarks.Scenarios;

/// <summary>
/// Metodos identicos a dos magnitudes. Mide el instrumento, no el codigo: si las filas de una
/// misma categoria no quedan juntas, la corrida no es publicable.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ControlBenchmark
{
    private readonly byte[] _a = new byte[16], _b = new byte[16], _c = new byte[16];

    [BenchmarkCategory("~0.5ns lectura")]
    [Benchmark(Baseline = true, Description = "campo A")]
    public byte[] FieldA() => _a;

    [BenchmarkCategory("~0.5ns lectura")]
    [Benchmark(Description = "campo B")]
    public byte[] FieldB() => _b;

    [BenchmarkCategory("~0.5ns lectura")]
    [Benchmark(Description = "campo C")]
    public byte[] FieldC() => _c;

    // Magnitud de la ruta de request: una asignacion pequena del tamano de un request tipico.

    [BenchmarkCategory("~10ns alloc 32B")]
    [Benchmark(Description = "alloc A")]
    public byte[] AllocA() => new byte[32];

    [BenchmarkCategory("~10ns alloc 32B")]
    [Benchmark(Description = "alloc B")]
    public byte[] AllocB() => new byte[32];

    [BenchmarkCategory("~10ns alloc 32B")]
    [Benchmark(Description = "alloc C")]
    public byte[] AllocC() => new byte[32];
}
