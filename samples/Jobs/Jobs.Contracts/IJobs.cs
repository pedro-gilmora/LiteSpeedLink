using MemoryPack;
using SourceCrafter.LiteSpeedLink;

namespace Jobs;

public enum JobState { Queued, Running, Done, Failed }

[MemoryPackable]
public sealed partial record JobInfo(int Id, string Path, JobState State, long Done, long Total, string? Result);

/// <summary>API local del agente: la CLI encola trabajos y sondea su progreso.</summary>
public interface IJobs : IServiceUnit
{
    /// <summary>Encola el calculo del SHA-256 de un fichero (ruta absoluta) y devuelve el id del trabajo.</summary>
    int Submit(string path);

    JobInfo[] List();

    /// <summary>Sondeo barato: sobre memoria compartida una llamada cuesta microsegundos.</summary>
    JobInfo? Get(int jobId);
}

public static class Agent
{
    /// <summary>Nombre del canal local: shared memory en Windows, Unix domain socket en Linux/macOS.</summary>
    public const string Name = "lsl-jobs-agent";
}
