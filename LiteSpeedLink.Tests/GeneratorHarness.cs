using System.Reflection;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace LiteSpeedLink.Tests;

/// <summary>
/// Ejecuta en memoria el generador de DI (paquete) con el parcial de LiteSpeedLink cargado.
///
/// <para>El generador de DI descubre el parcial entre los ensamblados ya cargados cuyo nombre
/// empieza por <c>SourceCrafter.DependencyInjection.Partial</c>: por eso el parcial se carga
/// antes de crear el driver. Las rutas las fija el csproj (<c>AssemblyMetadata</c>).</para>
/// </summary>
static class GeneratorHarness
{
    internal sealed record Result(IReadOnlyDictionary<string, string> Sources, IReadOnlyList<Diagnostic> GeneratorDiagnostics, IReadOnlyList<Diagnostic> CompilationDiagnostics)
    {
        internal IEnumerable<Diagnostic> All => GeneratorDiagnostics.Concat(CompilationDiagnostics);

        internal IReadOnlyList<Diagnostic> Errors => [.. All.Where(d => d.Severity == DiagnosticSeverity.Error)];

        internal bool HasDiagnostic(string id) => All.Any(d => d.Id == id);
    }

    static string Meta(string key) =>
        typeof(GeneratorHarness).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == key).Value!;

    static readonly Lazy<IIncrementalGenerator> Generator = new(() =>
    {
        Assembly.LoadFrom(Meta("LslPartial"));

        var type = Assembly.LoadFrom(Meta("DiGenerator")).GetTypes()
            .First(t => typeof(IIncrementalGenerator).IsAssignableFrom(t) && !t.IsAbstract);

        return (IIncrementalGenerator)Activator.CreateInstance(type)!;
    });

    static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(() =>
    {
        List<MetadataReference> refs = [];

        foreach (var dll in Directory.EnumerateFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll"))
        {
            // El directorio compartido mezcla ensamblados nativos sin metadatos.
            try { refs.Add(MetadataReference.CreateFromFile(dll)); } catch (Exception) { }
        }

        foreach (var name in (string[])["LiteSpeedLink.Abstractions", "SourceCrafter.LiteSpeedLink.Server", "SourceCrafter.LiteSpeedLink.Client",
            "SourceCrafter.DependencyInjection.Metadata", "MemoryPack.Core", "SharedMemory"])
            refs.Add(MetadataReference.CreateFromFile(Path.Combine(AppContext.BaseDirectory, name + ".dll")));

        return refs;
    });

    static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Preview)
        .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", "Probe")]);

    const string GlobalUsings = "global using System; global using System.Threading; global using System.Threading.Tasks; global using System.Collections.Generic;";

    internal static Result Run(string source)
    {
        var compilation = CSharpCompilation.Create(
            "Probe",
            [
                CSharpSyntaxTree.ParseText(source, ParseOptions, "Probe.cs"),
                CSharpSyntaxTree.ParseText(GlobalUsings, ParseOptions, "Usings.cs"),
                CSharpSyntaxTree.ParseText(File.ReadAllText(Meta("InternalAttributes")), ParseOptions, "Attributes.cs"),
            ],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: true));

        var driver = CSharpGeneratorDriver.Create([Generator.Value.AsSourceGenerator()], parseOptions: ParseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        var run = driver.GetRunResult();

        return new(
            run.GeneratedTrees.ToDictionary(t => Path.GetFileName(t.FilePath), t => t.GetText().ToString()),
            [.. run.Diagnostics],
            [.. output.GetDiagnostics().Where(d => d.Id != "CS0009")]);
    }
}
