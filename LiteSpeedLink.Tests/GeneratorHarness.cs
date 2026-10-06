using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

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
        Assembly.LoadFrom(Meta("LslServerPartial"));
        Assembly.LoadFrom(Meta("LslClientPartial"));

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
            "SourceCrafter.DependencyInjection.Metadata", "MemoryPack.Core"])
            refs.Add(MetadataReference.CreateFromFile(Path.Combine(AppContext.BaseDirectory, name + ".dll")));

        return refs;
    });

    static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Preview)
        .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", "Probe")]);

    const string GlobalUsings = "global using System; global using System.Threading; global using System.Threading.Tasks; global using System.Collections.Generic;";

    internal static Result Run(string source, IReadOnlyDictionary<string, string>? globalOptions = null)
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

        var driver = CSharpGeneratorDriver.Create([Generator.Value.AsSourceGenerator()], parseOptions: ParseOptions,
                optionsProvider: globalOptions is null ? null : new Options(globalOptions))
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        var run = driver.GetRunResult();

        return new(
            run.GeneratedTrees.ToDictionary(t => Path.GetFileName(t.FilePath), t => t.GetText().ToString()),
            [.. run.Diagnostics],
            [.. output.GetDiagnostics().Where(d => d.Id != "CS0009")]);
    }

    /// <summary>Equivale a las lineas <c>build_property.*</c> del editorconfig global de MSBuild.</summary>
    sealed class Options(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptionsProvider
    {
        sealed class Set(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) => values.TryGetValue(key, out value);
        }

        static readonly Set Empty = new(new Dictionary<string, string>());

        public override AnalyzerConfigOptions GlobalOptions { get; } = new Set(values);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Empty;
    }
}
