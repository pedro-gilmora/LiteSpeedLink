using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace LiteSpeedLink.Benchmarks;

/// <summary>
/// Configuracion del banco. Misma politica que <c>SourceCrafter.DependencyInjection\Benchmarks.HandCoded</c>:
/// <list type="number">
///   <item><b>Afinidad a los P-cores</b>, uno por nucleo fisico (logicos 0, 2, ..., 14 del i9-14900HX).
///   Sin ella el planificador reparte filas entre P y E-cores y fabrica diferencias de 2x que no existen.</item>
///   <item><b>LaunchCount = 1</b>: con la afinidad fijada, promediar procesos ya no corrige nada.</item>
///   <item><b>MemoryRandomization</b> en el arnes de publicacion.</item>
/// </list>
/// <para>
/// <b>Protocolo de lectura:</b> se mira primero <see cref="Scenarios.ControlBenchmark"/>. Si sus metodos
/// identicos no quedan agrupados, ninguna otra tabla de la corrida es publicable. La columna
/// <c>Allocated</c> es un recuento y es fiable siempre, tambien en <see cref="Fast"/>.
/// </para>
/// <para>
/// Los escenarios TCP hacen I/O real sobre loopback: sus tiempos estan en microsegundos y su banda de
/// ruido es mucho mayor que la del control de nanosegundos. Se comparan entre si, no contra el control.
/// </para>
/// </summary>
public static class HarnessConfig
{
    private const long PerformanceCores = 0x0000000000005555;

    public static IConfig Instance { get; } = ManualConfig
        .Create(DefaultConfig.Instance)
        .AddJob(Job.Default
            .WithAffinity((IntPtr)PerformanceCores)
            .WithLaunchCount(1)
            .WithWarmupCount(10)
            .WithIterationCount(15)
            .WithMemoryRandomization());

    /// <summary>Iteracion rapida. Los tiempos NO son publicables; las asignaciones si.</summary>
    public static IConfig Fast { get; } = ManualConfig
        .Create(DefaultConfig.Instance)
        .AddJob(Job.Default
            .WithAffinity((IntPtr)PerformanceCores)
            .WithLaunchCount(1)
            .WithWarmupCount(3)
            .WithIterationCount(5));
}
