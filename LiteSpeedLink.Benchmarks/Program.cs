using BenchmarkDotNet.Running;

namespace LiteSpeedLink.Benchmarks;

/// <summary>
/// Punto de entrada del banco. Mismo contrato que <c>Benchmarks.HandCoded</c>:
/// <list type="bullet">
///   <item><c>--check</c>: solo la verificacion semantica.</item>
///   <item><c>--list</c> o sin argumentos: imprime la tabla de <see cref="Suite"/> y sale.</item>
///   <item><b>un entero</b>, hexadecimal o nombres separados por coma: mascara de <see cref="Suite"/>.</item>
///   <item><c>--fast</c>: arnes rapido. Asignaciones fiables; tiempos no.</item>
///   <item>el resto se pasa tal cual a BenchmarkDotNet.</item>
/// </list>
/// <para>
/// <b>La verificacion semantica corre SIEMPRE antes de medir, y si falla se aborta.</b>
/// </para>
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--check"))
        {
            return SemanticCheck.Run();
        }

        var fast = args.Contains("--fast");
        var rest = args.Where(a => a != "--fast").ToArray();

        if (rest.Contains("--list") || rest.Length == 0)
        {
            SuiteMap.PrintLegend();
            return 0;
        }

        var suite = Suite.None;
        var filters = new List<string>();

        foreach (var arg in rest)
        {
            if (!arg.StartsWith('-') && SuiteMap.TryParse(arg, out var parsed))
            {
                suite |= parsed;
            }
            else
            {
                filters.Add(arg);
            }
        }

        var types = SuiteMap.Resolve(suite);

        if (suite != Suite.None && types.Length == 0)
        {
            Console.WriteLine($"La mascara {(int)suite} no selecciona ninguna tabla.");
            Console.WriteLine();
            SuiteMap.PrintLegend();
            return 1;
        }

        if (SemanticCheck.Run() != 0)
        {
            Console.WriteLine();
            Console.WriteLine("Medicion ABORTADA: la semantica no es correcta, las cifras no significarian nada.");
            return 1;
        }

        var config = fast ? HarnessConfig.Fast : HarnessConfig.Instance;

        Console.WriteLine();
        Console.WriteLine(fast
            ? "Arnes RAPIDO. Los tiempos NO son publicables; las asignaciones si."
            : "Arnes de PUBLICACION. Leer primero la tabla del control.");

        if (types.Length > 0)
        {
            Console.WriteLine($"Seleccion: {suite} ({(int)suite}) -> {types.Length} tabla(s).");

            if ((suite & Suite.Control) == 0)
                Console.WriteLine("AVISO: sin el control (bit 0), estas cifras no son publicables.");

            Console.WriteLine();

            if (filters.Count == 0)
            {
                filters.Add("--filter");
                filters.Add("*");
            }

            BenchmarkSwitcher.FromTypes(types).Run([.. filters], config);
        }
        else
        {
            Console.WriteLine();
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run([.. filters], config);
        }

        return 0;
    }
}
