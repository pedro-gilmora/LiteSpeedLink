using BenchmarkDotNet.Attributes;
using MemoryPack;
using SourceCrafter.LiteSpeedLink;

namespace LiteSpeedLink.Benchmarks.Scenarios;

/// <summary>
/// Detección de errores en el cliente: excepción de MemoryPack como control de flujo (original TCP)
/// frente a un byte de estado al inicio de la respuesta (propuesta 3.4).
/// </summary>
[MemoryDiagnoser]
public class ResponseStatusBenchmarks
{
    private byte[] _okLegacy = [], _failLegacy = [], _okStatus = [], _failStatus = [];

    [GlobalSetup]
    public void Setup()
    {
        _okLegacy = MemoryPackSerializer.Serialize("hola pedro");
        _failLegacy = MemoryPackSerializer.Serialize((ResponseStatus.Failed, "boom"));

        _okStatus = [(byte)ResponseStatus.Success, .. MemoryPackSerializer.Serialize("hola pedro")];
        _failStatus = [(byte)ResponseStatus.Failed, .. MemoryPackSerializer.Serialize("boom")];
    }

    [Benchmark(Baseline = true)]
    public string? Legacy_Success() => MemoryPackSerializer.Deserialize<string>(_okLegacy);

    /// <summary>Error forzando que el tipo esperado no case: provoca la excepción del flujo original.</summary>
    [Benchmark]
    public string? Legacy_Failure_ViaException()
    {
        try
        {
            return MemoryPackSerializer.Deserialize<Credentials>(_failLegacy).User;
        }
        catch (MemoryPackSerializationException)
        {
            return MemoryPackSerializer.Deserialize<(ResponseStatus, string)>(_failLegacy).Item2;
        }
    }

    [Benchmark]
    public string? Status_Success() => Read(_okStatus);

    [Benchmark]
    public string? Status_Failure() => Read(_failStatus);

    private static string? Read(byte[] response) => (ResponseStatus)response[0] switch
    {
        ResponseStatus.Success => MemoryPackSerializer.Deserialize<string>(response.AsSpan(1)),
        _ => MemoryPackSerializer.Deserialize<string>(response.AsSpan(1)),
    };
}
