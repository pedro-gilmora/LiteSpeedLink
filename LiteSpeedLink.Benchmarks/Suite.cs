using LiteSpeedLink.Benchmarks.Scenarios;

namespace LiteSpeedLink.Benchmarks;

/// <summary>
/// Seleccion de tablas por mascara de bits. El control esta en el bit 0: sumar 1 a la mascara
/// que interese es la costumbre correcta y la mas corta.
/// </summary>
[Flags]
public enum Suite
{
    None = 0,

    /// <summary>Metodos identicos. Mide el instrumento, no el codigo.</summary>
    Control = 1 << 0,

    /// <summary>Construccion del request en el cliente: opId + cuerpo MemoryPack.</summary>
    RequestBuilding = 1 << 1,

    /// <summary>Lectura del opId y deserializacion del cuerpo en el servidor.</summary>
    ServerParsing = 1 << 2,

    /// <summary>Byte de estado frente a excepcion de MemoryPack como control de flujo.</summary>
    ResponseStatus = 1 << 3,

    /// <summary>PoC-A: SemaphoreSlim frente a multiplexado sobre una conexion TCP real.</summary>
    TcpConcurrency = 1 << 4,

    /// <summary>6.1: streaming por transporte real (Memory, UDS, TCP).</summary>
    Streaming = 1 << 5,

    /// <summary>6.1: N llamadas concurrentes sobre una MemoryConnection (UDS de referencia).</summary>
    MemoryConcurrency = 1 << 6,

    /// <summary>Plana: LiteSpeedLink TCP vs ASP.NET Core (full/slim) vs gRPC (full/slim), misma operacion.</summary>
    Comparison = 1 << 7,

    /// <summary>La ruta de request/response completa, sin red.</summary>
    Wire = RequestBuilding | ServerParsing | ResponseStatus,

    All = Control | Wire | TcpConcurrency | Streaming | MemoryConcurrency
}

public static class SuiteMap
{
    private static readonly (Suite Flag, Type Type)[] Entries =
    [
        (Suite.Control, typeof(ControlBenchmark)),
        (Suite.RequestBuilding, typeof(RequestBuildingBenchmarks)),
        (Suite.ServerParsing, typeof(ServerParsingBenchmarks)),
        (Suite.ResponseStatus, typeof(ResponseStatusBenchmarks)),
        (Suite.TcpConcurrency, typeof(TcpConcurrencyBenchmarks)),
        (Suite.Streaming, typeof(StreamingBenchmarks)),
        (Suite.MemoryConcurrency, typeof(MemoryConcurrencyBenchmarks)),
        (Suite.Comparison, typeof(Comparison.ComparisonBenchmarks)),
    ];

    public static Type[] Resolve(Suite suite) =>
        [.. Entries.Where(e => (suite & e.Flag) == e.Flag).Select(e => e.Type)];

    public static bool TryParse(string text, out Suite suite)
    {
        suite = Suite.None;

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var hex))
        {
            suite = (Suite)hex;
            return true;
        }

        if (int.TryParse(text, out var value))
        {
            suite = (Suite)value;
            return true;
        }

        return Enum.TryParse(text, ignoreCase: true, out suite) && suite != Suite.None;
    }

    public static void PrintLegend()
    {
        Console.WriteLine("Tablas disponibles. Suma los valores y pasalos como un solo entero:");
        Console.WriteLine();
        Console.WriteLine("  valor  nombre");
        Console.WriteLine("  -----  ------------------------------");

        foreach (var (flag, _) in Entries)
            Console.WriteLine($"  {(int)flag,5}  {flag}");

        Console.WriteLine();
        Console.WriteLine("  Combinaciones con nombre propio:");
        Console.WriteLine($"  {(int)Suite.Wire,5}  Wire  (request/response completo, sin red)");
        Console.WriteLine($"  {(int)Suite.All,5}  All");
        Console.WriteLine();
        Console.WriteLine("  Ejemplos:");
        Console.WriteLine("    dotnet run -c Release -- --check       solo verificacion semantica");
        Console.WriteLine("    dotnet run -c Release -- 15            control + Wire");
        Console.WriteLine("    dotnet run -c Release -- 17 --fast     control + TCP, iterar rapido");
    }
}
