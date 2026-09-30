using Microsoft.CodeAnalysis;
using SourceCrafter.DependencyInjection.Generation;
using SourceCrafter.LiteSpeedLink.Helpers;

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

public partial class ServiceHandlersGenerator
{
    private const string PipelineNs = "SourceCrafter.LiteSpeedLink";

    private enum PipeKind : byte { Sync, Task, ValueTask }

    private sealed class Stage(INamedTypeSymbol pipeline, INamedTypeSymbol iface, ITypeSymbol @in, ITypeSymbol @out, PipeKind kind)
    {
        public ServiceInfo Service { get; set; } = null!;
        public INamedTypeSymbol Pipeline { get; } = pipeline;
        public INamedTypeSymbol Iface { get; } = iface;
        public ITypeSymbol In { get; } = @in;
        public ITypeSymbol Out { get; } = @out;
        public PipeKind Kind { get; } = kind;
    }

    private static readonly DiagnosticDescriptor
        NotAPipeline = new("SCLSL010", "Processor type is not a pipeline",
            "'{0}' must implement IPipeline, IAsyncPipeline or IAsyncValuePipeline", "LiteSpeedLink", DiagnosticSeverity.Error, true),
        PipelineMismatch = new("SCLSL011", "Pipeline chain type mismatch",
            "{0}: '{1}' can't flow into '{2}'", "LiteSpeedLink", DiagnosticSeverity.Error, true),
        PipelineUnsupported = new("SCLSL012", "Pipeline not supported here",
            "{0}", "LiteSpeedLink", DiagnosticSeverity.Error, true),
        PipelineNotRegistered = new("SCLSL013", "Pipeline is not registered",
            "'{0}' must be registered as a service of '{1}' (and exposed as a member)", "LiteSpeedLink", DiagnosticSeverity.Error, true),
        PipelineNotImplicit = new("SCLSL014", "Pipeline must be implemented implicitly",
            "'{0}' must implement a single pipeline interface with a public, implicit Process/ProcessAsync", "LiteSpeedLink", DiagnosticSeverity.Error, true);

    private static bool IsProcessorAttribute(AttributeData a, out string name)
    {
        name = a.AttributeClass is { IsGenericType: true } ac && ac.ContainingNamespace?.ToDisplayString() == PipelineNs
            ? ac.Name
            : "";

        return name is "ProcessorAttribute" or "PreProcessorAttribute" or "PostProcessorAttribute"
            or "ClientPreProcessorAttribute" or "ServerPreProcessorAttribute"
            or "ClientPostProcessorAttribute" or "ServerPostProcessorAttribute";
    }

    private static bool HasProcessors(ImmutableArray<AttributeData> attrs) => attrs.Any(a => IsProcessorAttribute(a, out _));

    /// <summary>Etapas que aplica un lado (host o cliente), en el orden declarado.</summary>
    private static List<Stage> GetStages(ImmutableArray<AttributeData> attrs, bool server, ServiceProviderInfo? container, PartialContribution contribution, Location? location)
    {
        List<Stage> stages = [];

        foreach (var attr in attrs)
        {
            if (!IsProcessorAttribute(attr, out var name)) continue;

            if (name.StartsWith(server ? "Client" : "Server")) continue;

            if (attr.AttributeClass!.TypeArguments[0] is not INamedTypeSymbol pipeline) continue;

            if (!TryGetStage(pipeline, out var stage))
            {
                contribution.ReportDiagnostic(Diagnostic.Create(NotAPipeline, location, pipeline.ToDisplayString()));
                continue;
            }

            if (!IsImplicit(stage))
            {
                contribution.ReportDiagnostic(Diagnostic.Create(PipelineNotImplicit, location, pipeline.ToDisplayString()));
                continue;
            }

            // Los del otro lado solo fijan tipos: no se resuelven aqui.
            if (container is null) { stages.Add(stage); continue; }

            // Se prefiere el registro por implementacion; si no, el expuesto como el propio pipeline.
            var service = container.Services.FirstOrDefault(s => s.MemberName != null && SymbolEqualityComparer.Default.Equals(s.ImplType, pipeline))
                ?? container.Services.FirstOrDefault(s => s.MemberName != null && SymbolEqualityComparer.Default.Equals(s.ExportType, pipeline));

            if (service is null)
            {
                contribution.ReportDiagnostic(Diagnostic.Create(PipelineNotRegistered, location, pipeline.ToDisplayString(), container.ContainerType.ToDisplayString()));
                continue;
            }

            stage.Service = service;
            stages.Add(stage);
        }

        return stages;
    }

    private static bool TryGetStage(INamedTypeSymbol pipeline, out Stage stage)
    {
        foreach (var i in pipeline.AllInterfaces)
        {
            if (i.ContainingNamespace?.ToDisplayString() != PipelineNs) continue;

            PipeKind? kind = i.Name switch
            {
                "IPipeline" => PipeKind.Sync,
                "IAsyncPipeline" => PipeKind.Task,
                "IAsyncValuePipeline" => PipeKind.ValueTask,
                _ => null
            };

            if (kind is not { } k || i.TypeArguments is not { Length: 1 or 2 } args) continue;

            stage = new(pipeline, i, args[0], args[^1], k);
            return true;
        }

        stage = null!;
        return false;
    }

    /// <summary>Una sola interfaz de pipeline con su metodo publico e implicito: la llamada no necesita cast.</summary>
    private static bool IsImplicit(Stage stage)
    {
        if (stage.Pipeline.AllInterfaces.Count(i => i.ContainingNamespace?.ToDisplayString() == PipelineNs
                && i.Name is "IPipeline" or "IAsyncPipeline" or "IAsyncValuePipeline") != 1)
            return false;

        return stage.Iface.GetMembers().OfType<IMethodSymbol>().All(m =>
            stage.Pipeline.FindImplementationForInterfaceMember(m) is IMethodSymbol { DeclaredAccessibility: Accessibility.Public, ExplicitInterfaceImplementations.Length: 0 });
    }

    /// <summary>Valida <c>start -> etapas -> end</c>
    private static bool ValidateChain(Compilation compilation, ITypeSymbol? start, List<Stage> stages, ITypeSymbol? end,
        string what, PartialContribution contribution, Location? location)
    {
        var ok = true;
        var current = start;

        foreach (var stage in stages)
        {
            ok &= Link(current, stage.In);
            current = stage.Out;
        }

        return ok & Link(current, end);

        bool Link(ITypeSymbol? from, ITypeSymbol? to)
        {
            if (from is null || to is null || compilation.HasImplicitConversion(from, to)) return true;

            contribution.ReportDiagnostic(Diagnostic.Create(PipelineMismatch, location, what, from.ToDisplayString(), to.ToDisplayString()));
            return false;
        }
    }

    /// <summary>
    /// Envuelve <paramref name="expr"/> con las etapas. Un estado distinto de Success lanza y lo
    /// convierte en Failed el catch del handler (host) o llega al llamador (cliente).
    /// </summary>
    // ponytail: rechazo via excepcion (coste de throw en el camino de fallo); si pesa, emitir early-return con el estado.
    // ponytail: un pipeline Scoped se resuelve desde el scope del handler solo si este ya abrio uno; si no, desde la raiz.
    private static string StageVar(string root, int n) => "__" + root.TrimStart('_') + "_" + n;

    /// <summary>
    /// Una sentencia <c>if (etapa is not (Success, TOut x)) throw</c> por etapa, de dentro afuera.
    /// La ultima variable se llama <paramref name="last"/> (o <c>StageVar(root, n)</c>).
    /// </summary>
    // ponytail: patron con el tipo declarado: un TOut referencia null con Success tambien se rechaza.
    // Nullable<T> no admite patron de tipo (CS8116): ahi se usa 'var'.
    private static string ApplyStages(string expr, List<Stage> stages, bool canAwait, string provider, string indent, string? last = null)
    {
        var code = new StringBuilder();
        var root = expr;

        for (var n = 0; n < stages.Count; n++)
        {
            var s = stages[n];
            var resolve = provider + s.Service.MemberName + (s.Service.MemberIsMethodShaped ? "()" : null);

            if (s.Service.AsyncKind is not PartialAsyncKind.None)
                resolve = canAwait ? "(await " + resolve + ".ConfigureAwait(false))" : resolve + ".GetAwaiter().GetResult()";

            var call = s.Kind is PipeKind.Sync
                ? resolve + ".Process(" + expr + ")"
                : canAwait
                    ? "await " + resolve + ".ProcessAsync(" + expr + ").ConfigureAwait(false)"
                    : resolve + ".ProcessAsync(" + expr + ").GetAwaiter().GetResult()";

            expr = n == stages.Count - 1 && last != null ? last : StageVar(root, n);

            var type = s.Out.OriginalDefinition.SpecialType is SpecialType.System_Nullable_T
                ? "var"
                : s.Out.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            code.Append("if (").Append(call).Append(" is not (global::SourceCrafter.LiteSpeedLink.ResponseStatus.Success, ").Append(type).Append(' ').Append(expr).Append("))\n")
                .Append(indent).Append("    throw new global::SourceCrafter.LiteSpeedLink.PipelineRejectedException(global::SourceCrafter.LiteSpeedLink.ResponseStatus.Failed, \"")
                .Append(s.Pipeline.Name).Append("\");\n").Append(n == stages.Count - 1 ? "\n" : null).Append(indent);
        }

        return code.ToString();
    }

    /// <summary>
    /// Cliente de un metodo con procesadores. La firma publica la fijan la primera etapa de cada
    /// parametro y la ultima del retorno; si difiere del contrato, este se implementa de forma
    /// explicita lanzando <c>InvalidOperationException</c>.
    /// </summary>
    private static bool TryGenerateProcessedClientMethod(
        StringBuilder code, StringBuilder helpers, ServiceProviderInfo container, INamedTypeSymbol iFace, IMethodSymbol method, PartialContribution contribution, ref int rawIndex)
    {
        if (!HasProcessors(method.GetReturnTypeAttributes()) && !method.Parameters.Any(p => HasProcessors(p.GetAttributes())))
            return false;

        var compilation = container.Compilation;
        var location = method.Locations.FirstOrDefault();
        var isTask = method.ReturnType.TryGetAsyncType(out var retType, out var hasRet, out var isValueTask);
        hasRet = isTask ? hasRet : !method.ReturnsVoid;

        string
            name = method.Name,
            asyncName = name.EndsWith("Async") ? name : name + "Async",
            contractParams = string.Join(", ", method.Parameters.Select(p => p.Type.GlobalNamespaced + " " + p.Name)),
            contractArgs = string.Join(", ", method.Parameters.Select(p => p.Name)),
            explicitHead = "\n\n    " + method.ReturnType.GlobalNamespaced + " " + iFace.GlobalNamespaced + "." + name + "(" + contractParams + ")";

        if (method.Parameters.Any(p => p.RefKind is not RefKind.None)
            || (hasRet && retType.GlobalNonGenericNamespace is "global::System.Collections.Generic.IAsyncEnumerable" or "global::System.Collections.Generic.IEnumerable"))
        {
            contribution.ReportDiagnostic(Diagnostic.Create(PipelineUnsupported, location,
                $"{method.Name}: processors don't support ref/out/in parameters nor streamed results yet"));

            code.Append(explicitHead).Append(" => throw new global::System.InvalidOperationException(\"Processors not supported on ").Append(name).Append("\");");
            return true;
        }

        string? tokenName = null;
        List<(IParameterSymbol Param, ITypeSymbol ClientType, ITypeSymbol Wire, List<Stage> Stages)> inputs = [];

        foreach (var p in method.Parameters)
        {
            if (tokenName is null && p.Type.GlobalNamespaced == cancelTokenFullTypeName) { tokenName = p.Name; continue; }

            var attrs = p.GetAttributes();
            var clientStages = GetStages(attrs, false, container, contribution, location);
            var serverStages = GetStages(attrs, true, null, contribution, location);
            var wire = serverStages.Count > 0 ? serverStages[0].In : p.Type;

            ValidateChain(compilation, null, clientStages, wire, name + "(" + p.Name + ") client -> server", contribution, location);

            inputs.Add((p, clientStages.Count > 0 ? clientStages[0].In : wire, wire, clientStages));
        }

        var retAttrs = method.GetReturnTypeAttributes();
        var serverPost = GetStages(retAttrs, true, null, contribution, location);
        var clientPost = GetStages(retAttrs, false, container, contribution, location);

        if (!hasRet && (serverPost.Count > 0 || clientPost.Count > 0))
            contribution.ReportDiagnostic(Diagnostic.Create(PipelineUnsupported, location, name + ": post-processors need a return value"));

        var wireRet = serverPost.Count > 0 ? serverPost[^1].Out : retType;
        var finalRet = clientPost.Count > 0 ? clientPost[^1].Out : wireRet;

        ValidateChain(compilation, wireRet, clientPost, null, name + " server -> client response", contribution, location);

        bool matches = inputs.All(i => SymbolEqualityComparer.Default.Equals(i.ClientType, i.Param.Type))
            && (!hasRet || SymbolEqualityComparer.Default.Equals(finalRet, retType));

        var serviceId = GetServiceId(method.GlobalNamespaced);
        var clientParams = string.Join(", ", method.Parameters.Select(p =>
            (inputs.FirstOrDefault(i => SymbolEqualityComparer.Default.Equals(i.Param, p)).ClientType ?? p.Type).GlobalNamespaced + " " + p.Name));
        int n = rawIndex++;
        string? finalType = hasRet ? finalRet.GlobalNamespaced : null;
        string
            resName = "__Res" + n,
            reqArgs = inputs.Count > 0 ? "__Req" + n + "(" + string.Join(", ", inputs.Select(Wire)) + ")" : "default";

        if (inputs.Count > 0) EmitWriter(helpers, "__Req" + n, [.. inputs.Select(i => i.Wire)], "");
        if (hasRet) EmitReader(helpers, resName, [wireRet], "");

        static string Wire((IParameterSymbol Param, ITypeSymbol ClientType, ITypeSymbol Wire, List<Stage> Stages) i) => i.Stages.Count > 0 ? StageVar(i.Param.Name, i.Stages.Count - 1) : i.Param.Name;

        void Body(bool isAsync, string? token)
        {
            code.Append("\n        ");

            foreach (var i in inputs.Where(i => i.Stages.Count > 0))
                code.Append(ApplyStages(i.Param.Name, i.Stages, isAsync, "__provider.", "        "));

            if (hasRet) code.Append("var __r = ").Append(resName).Append("((");

            if (isAsync) code.Append("await ");

            code.Append("__connection.GetRaw").Append(isAsync ? "Async" : null).Append('(').Append(serviceId).Append(", ").Append(reqArgs);

            if (token != null) code.Append(", ").Append(token);

            code.Append(isAsync ? ").ConfigureAwait(false)" : ")");

            if (hasRet)
                code.Append(").Span);\n\n        ").Append(ApplyStages("__r", clientPost, isAsync, "__provider.", "        "))
                    .Append("return ").Append(clientPost.Count > 0 ? StageVar("__r", clientPost.Count - 1) : "__r");

            code.Append(";\n    }");
        }

        code.Append("\n\n    public async global::System.Threading.Tasks.ValueTask").Append(finalType is null ? null : "<" + finalType + ">")
            .Append(' ').Append(asyncName).Append('(').Append(clientParams);

        if (tokenName is null)
            code.Append(clientParams.Length > 0 ? ", " : null).Append(cancelTokenFullTypeName).Append(" @__token = default");

        code.Append(")\n    {");
        Body(true, tokenName ?? "@__token");

        if (!isTask)
        {
            code.Append("\n\n    public ").Append(finalType ?? "void").Append(' ').Append(name).Append('(').Append(clientParams).Append(")\n    {");
            Body(false, tokenName);
        }

        var mismatch = $"{iFace.Name}.{name} server contract ({string.Join(", ", inputs.Select(i => i.Param.Type.ToDisplayString()))}) -> {(hasRet ? retType.ToDisplayString() : "void")} differs from the client pipelines ({string.Join(", ", inputs.Select(i => i.ClientType.ToDisplayString()))}) -> {(hasRet ? finalRet.ToDisplayString() : "void")}; call {(isTask ? asyncName : name)}({string.Join(", ", inputs.Select(i => i.ClientType.ToDisplayString()))}) instead.";

        if (!matches)
            code.Append(explicitHead).Append(" => throw new global::System.InvalidOperationException(\"").Append(mismatch.Replace("\"", "\\\"")).Append("\");");
        else if (isTask)
            code.Append(explicitHead).Append(" => ").Append(asyncName).Append('(').Append(contractArgs).Append(')').Append(isValueTask ? null : ".AsTask()").Append(';');
        // sync + firma igual: el metodo publico ya implementa el contrato de forma implicita.

        return true;
    }
}
