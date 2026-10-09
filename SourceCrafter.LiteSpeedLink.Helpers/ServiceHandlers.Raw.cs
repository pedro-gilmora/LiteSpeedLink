using Microsoft.CodeAnalysis;
using SourceCrafter.DependencyInjection.Generation;
using SourceCrafter.LiteSpeedLink.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public partial class ServiceHandlersGenerator
{
    private enum WireKind : byte { Unmanaged, String, Packable, Value }

    /// <summary>
    /// Metodo de lectura/escritura de MemoryPack decidido en compilacion. Los bloques secuenciales son
    /// byte a byte iguales a la tupla serializada, asi que ambos extremos siguen siendo compatibles.
    /// </summary>
    private static WireKind GetWireKind(ITypeSymbol t)
    {
        return t switch
        {
            { SpecialType: SpecialType.System_String } => WireKind.String,
            { TypeKind: TypeKind.Enum } => WireKind.Unmanaged,
            { SpecialType: >= SpecialType.System_Boolean and <= SpecialType.System_Double } => WireKind.Unmanaged,
            _ => t.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "MemoryPack.MemoryPackableAttribute") 
                ? WireKind.Packable 
                : WireKind.Value
        };
    }

    private static string WireTypeName(ITypeSymbol t) =>
        (t.IsReferenceType ? t.WithNullableAnnotation(NullableAnnotation.NotAnnotated) : t).GlobalNamespaced;

    private static string WriteCall(ITypeSymbol t, string value) => GetWireKind(t) switch
    {
        WireKind.Unmanaged => $"__w.WriteUnmanaged({value});",
        WireKind.String => $"__w.WriteString({value});",
        WireKind.Packable => $"__w.WritePackable({value});",
        _ => $"__w.WriteValue({value});"
    };

    private static string ReadCall(ITypeSymbol t) => GetWireKind(t) switch
    {
        WireKind.Unmanaged => $"__r.ReadUnmanaged<{WireTypeName(t)}>()",
        WireKind.String => "__r.ReadString()!",
        WireKind.Packable => $"__r.ReadPackable<{WireTypeName(t)}>()!",
        _ => $"__r.ReadValue<{WireTypeName(t)}>()!"
    };

    /// <summary>Bytes exactos de un primitivo/enum en MemoryPack (WriteUnmanaged); null si depende del valor.</summary>
    private static int? FixedSize(ITypeSymbol t) => (t is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } u } ? u : t).SpecialType switch
    {
        SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte => 1,
        SpecialType.System_Char or SpecialType.System_Int16 or SpecialType.System_UInt16 => 2,
        SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Single => 4,
        SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Double => 8,
        _ => null
    };

    private static string TupleOf(IReadOnlyList<ITypeSymbol> types) =>
        types.Count == 1 ? types[0].GlobalNamespaced : "(" + string.Join(", ", types.Select(t => t.GlobalNamespaced)) + ")";

    /// <summary>Capacidad inicial del escritor: constante plegada + termino por cada string.</summary>
    private static string CapacityOf(IReadOnlyList<ITypeSymbol> types)
    {
        int constant = types.Sum(t => FixedSize(t) ?? (GetWireKind(t) is WireKind.String ? 8 : 64));
        var strings = types.Select((t, i) => (t, i)).Where(x => GetWireKind(x.t) is WireKind.String).Select(x => $"(__p{x.i}?.Length ?? 0) * 3");

        return string.Join(" + ", new[] { constant.ToString() }.Concat(strings));
    }

    /// <summary>
    /// Lector estatico: <c>ReadOnlySpan&lt;byte&gt;</c> -> valor o tupla, con los metodos especificos de cada tipo.
    /// Con <paramref name="outFrom"/> (0 o 1) tiene la firma del contrato: los tipos desde ese indice salen por <c>out __o{k}</c>.
    /// </summary>
    private static void EmitReader(StringBuilder code, string name, IReadOnlyList<ITypeSymbol> types, string indent, int outFrom = -1)
    {
        bool outs = outFrom >= 0 && outFrom < types.Count;

        code.Append("\n\n").Append(indent).Append("private static ").Append(!outs ? TupleOf(types) : outFrom == 0 ? "void" : types[0].GlobalNamespaced)
            .Append(' ').Append(name).Append("(global::System.ReadOnlySpan<byte> __b");

        for (int i = outs ? outFrom : types.Count; i < types.Count; i++)
            code.Append(", out ").Append(types[i].GlobalNamespaced).Append(" __o").Append(i - outFrom);

        code.Append(")\n")
            .Append(indent).Append("{\n")
            .Append(indent).Append("    using var __s = global::MemoryPack.MemoryPackReaderOptionalStatePool.Rent(null);\n")
            .Append(indent).Append("    var __r = new global::MemoryPack.MemoryPackReader(__b, __s);\n");

        if (!outs)
        {
            code.Append(indent).Append("    return ")
                .Append(types.Count == 1 ? ReadCall(types[0]) : "(" + string.Join(", ", types.Select(ReadCall)) + ")")
                .Append(";\n");
        }
        else
        {
            // Orden del cable: retorno primero, luego out/ref.
            if (outFrom == 1) code.Append(indent).Append("    var __v = ").Append(ReadCall(types[0])).Append(";\n");

            for (int i = outFrom; i < types.Count; i++)
                code.Append(indent).Append("    __o").Append(i - outFrom).Append(" = ").Append(ReadCall(types[i])).Append(";\n");

            if (outFrom == 1) code.Append(indent).Append("    return __v;\n");
        }

        code.Append(indent).Append('}');
    }

    /// <summary>Escritor estatico: argumentos -> bytes de la peticion (cuerpo sin cabecera).</summary>
    private static void EmitWriter(StringBuilder code, string name, IReadOnlyList<ITypeSymbol> types, string indent)
    {
        code.Append("\n\n").Append(indent).Append("private static global::System.ReadOnlyMemory<byte> ").Append(name).Append('(')
            .Append(string.Join(", ", types.Select((t, i) => t.GlobalNamespaced + " __p" + i))).Append(")\n")
            .Append(indent).Append("{\n")
            // ponytail: un ArrayBufferWriter por llamada; el cuerpo debe sobrevivir a los await del transporte. Upgrade: buffer del pool devuelto por el transporte.
            // Primitivos exactos; strings con cota UTF-8 (cabecera 8 + 3/char, sin recorrer el string: nunca crece); packables/valores parten de 64 y crecen.
            .Append(indent).Append("    var __buf = new global::System.Buffers.ArrayBufferWriter<byte>(")
            .Append(CapacityOf(types)).Append(");\n")
            .Append(indent).Append("    using (var __s = global::MemoryPack.MemoryPackWriterOptionalStatePool.Rent(null))\n")
            .Append(indent).Append("    {\n")
            .Append(indent).Append("        var __w = new global::MemoryPack.MemoryPackWriter<global::System.Buffers.ArrayBufferWriter<byte>>(ref __buf, __s);\n");

        for (int i = 0; i < types.Count; i++)
            code.Append(indent).Append("        ").Append(WriteCall(types[i], "__p" + i)).Append('\n');

        code.Append(indent).Append("        __w.Flush();\n")
            .Append(indent).Append("    }\n")
            .Append(indent).Append("    return __buf.WrittenMemory;\n")
            .Append(indent).Append('}');
    }

    private const string ClientRetryAttr = "SourceCrafter.LiteSpeedLink.ClientRetryAttribute", ServerRetryAttr = "SourceCrafter.LiteSpeedLink.ServerRetryAttribute";

    private static readonly DiagnosticDescriptor RetryOnBothSides = new("SCLSL015", "Retry on both client and server",
        "'{0}' has [ClientRetry] and [ServerRetry]: attempts multiply (up to {1}) and so does worst-case latency", "LiteSpeedLink", DiagnosticSeverity.Warning, true);

    private static readonly DiagnosticDescriptor RetryOnStream = new("SCLSL016", "Retry ignored on streams",
        "'{0}' streams its result: [ClientRetry]/[ServerRetry] are ignored because retrying would replay items already delivered", "LiteSpeedLink", DiagnosticSeverity.Warning, true);

    /// <summary>IEnumerable&lt;T&gt;/IAsyncEnumerable&lt;T&gt; devuelto tal cual: se emite elemento a elemento.</summary>
    private static bool IsStream(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } n
            && n.ConstructedFrom.ToDisplayString() is "System.Collections.Generic.IAsyncEnumerable<T>" or "System.Collections.Generic.IEnumerable<T>";

    private const string ClientCacheAttr = "SourceCrafter.LiteSpeedLink.ClientCacheAttribute", ServerCacheAttr = "SourceCrafter.LiteSpeedLink.ServerCacheAttribute";

    private static readonly DiagnosticDescriptor CacheIgnored = new("SCLSL017", "Cache ignored",
        "'{0}': [{1}] is ignored because {2}", "LiteSpeedLink", DiagnosticSeverity.Warning, true);

    /// <summary>
    /// [ClientCache]/[ServerCache](durationMs, capacity) efectivo (lectura posicional, como <see cref="ReadRetry"/>);
    /// null si no lo lleva o no aplica, con SCLSL017: stream, sin resultado (comando), out/ref, o argumentos no positivos.
    /// </summary>
    private static (int DurationMs, int Capacity)? GetCache(IMethodSymbol method, bool server, PartialContribution contribution)
    {
        var attrName = server ? ServerCacheAttr : ClientCacheAttr;

        if (method.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == attrName) is not { } attr) return null;

        bool hasRet = method.ReturnType.TryGetAsyncType(out _, out var awaitedHasRet, out _) ? awaitedHasRet : !method.ReturnsVoid;
        int duration = attr.ConstructorArguments is [{ Value: int d }, ..] ? d : 0,
            capacity = attr.ConstructorArguments is [_, { Value: int c }] ? c : 0;

        string? why = IsStream(method.ReturnType) ? "streamed results have no single response"
            : !hasRet ? "it has no result (a command: caching would suppress its effects)"
            : method.Parameters.Any(p => p.RefKind is RefKind.Out or RefKind.Ref) ? "out/ref parameters are part of the result"
            : duration <= 0 || capacity <= 0 ? "durationMs and capacity must be positive"
            : null;

        if (why is null) return (duration, capacity);

        contribution.ReportDiagnostic(Diagnostic.Create(CacheIgnored, attr.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? method.Locations.FirstOrDefault(),
            method.Name, server ? "ServerCache" : "ClientCache", why));
        return null;
    }

    /// <summary>
    /// [ClientRetry]/[ServerRetry](attempts, intervalMs) leido del simbolo: Roslyn ya normaliza ConstructorArguments al orden
    /// del constructor (argumentos con nombre reubicados y defaults rellenados), asi que la lectura es posicional.
    /// </summary>
    private static (int Attempts, int IntervalMs)? ReadRetry(IMethodSymbol method, bool server) =>
        method.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == (server ? ServerRetryAttr : ClientRetryAttr))
            is { ConstructorArguments: [{ Value: int attempts }, { Value: int interval }] } && attempts > 1
                ? (attempts, interval)
                : null;

    /// <summary>
    /// Reintento efectivo; null si no lo lleva, no reintenta (attempts &lt; 2) o es un stream: el fallo llega a mitad de la
    /// enumeracion y repetir reenviaria lo ya entregado (reanudar exigiria un checkpoint en el contrato).
    /// </summary>
    private static (int Attempts, int IntervalMs)? GetRetry(IMethodSymbol method, bool server) =>
        IsStream(method.ReturnType) ? null : ReadRetry(method, server);

    /// <summary>SCLSL015 (ambos lados) y SCLSL016 (stream); lo emiten cliente y host (suelen compilarse en proyectos distintos).</summary>
    private static void ReportRetryDiagnostics(IMethodSymbol method, PartialContribution contribution)
    {
        var location = method.Locations.FirstOrDefault();

        if (IsStream(method.ReturnType))
        {
            if (ReadRetry(method, false) != null || ReadRetry(method, true) != null)
                contribution.ReportDiagnostic(Diagnostic.Create(RetryOnStream, location, method.Name));
        }
        else if (GetRetry(method, false) is { } c && GetRetry(method, true) is { } s)
            contribution.ReportDiagnostic(Diagnostic.Create(RetryOnBothSides, location, method.Name, c.Attempts * s.Attempts));
    }

    /// <summary>
    /// Sentencia de llamada al transporte (<paramref name="call"/>), con la respuesta en <paramref name="target"/> si no es null.
    /// Con [ClientRetry]/[ServerRetry] va en un bucle inline que repite solo esa llamada ante TimeoutException, nunca si el
    /// llamante cancelo: sin structs ni delegados por operacion; processors y lectores quedan fuera y se ejecutan una vez.
    /// </summary>
    private static void EmitCall(StringBuilder code, (int Attempts, int IntervalMs)? retry, string call, string? target, string? token, bool isAsync,
        string targetType = "global::System.ReadOnlyMemory<byte>")
    {
        if (retry is not { } r)
        {
            code.Append(target is null ? null : "var " + target + " = ").Append(call).Append(';');
            return;
        }

        if (target != null) code.Append(targetType).Append(' ').Append(target).Append(";\n\n        ");

        code.Append("for (var __attempt = 1; ; __attempt++)\n        {\n            try { ").Append(target is null ? null : target + " = ").Append(call)
            .Append("; break; }\n            catch (global::System.TimeoutException) when (__attempt < ").Append(r.Attempts)
            .Append(token is null ? null : " && !" + token + ".IsCancellationRequested").Append(") { }");

        if (r.IntervalMs > 0)
            code.Append("\n\n            ").Append(isAsync
                ? $"await global::System.Threading.Tasks.Task.Delay({r.IntervalMs}, {token}).ConfigureAwait(false);"
                : token is null
                    ? $"global::System.Threading.Thread.Sleep({r.IntervalMs});"
                    // GetResult y no Wait(): la cancelacion sale como TaskCanceledException, igual que en async.
                    : $"global::System.Threading.Tasks.Task.Delay({r.IntervalMs}, {token}).GetAwaiter().GetResult();");

        code.Append("\n        }");
    }

    /// <summary>
    /// Cliente unario raw:
    /// <c>GetRaw</c>/<c>GetRawAsync</c>; la respuesta se lee con lectores especificos. Streams y operaciones
    /// sin retorno se envian igual; el cuerpo de respuesta se ignora salvo out/ref. Streams y Task sin
    /// resultado siguen por la API tipada.
    /// </summary>
    private static bool TryGenerateRawClientMethod(StringBuilder code, StringBuilder helpers, INamedTypeSymbol iFace, IMethodSymbol method, string conn, ref int rawIndex,
        Compilation compilation, PartialContribution contribution)
    {
        bool isTask = method.ReturnType.TryGetAsyncType(out var retType, out var hasRet, out var isValueTask);
        hasRet = isTask ? hasRet : !method.ReturnsVoid;

        if (hasRet && retType.GlobalNonGenericNamespace is "global::System.Collections.Generic.IAsyncEnumerable" or "global::System.Collections.Generic.IEnumerable")
            return false;

        string? tokenName = null;
        List<IParameterSymbol> request = [], response = [];

        foreach (var p in method.Parameters)
        {
            if (tokenName is null && p.RefKind is RefKind.None && p.Type.GlobalNamespaced == cancelTokenFullTypeName) { tokenName = p.Name; continue; }

            if (p.RefKind is not RefKind.Out) request.Add(p);
            if (p.RefKind is RefKind.Out or RefKind.Ref) response.Add(p);
        }

        List<ITypeSymbol> responseTypes = [.. hasRet ? [retType] : Enumerable.Empty<ITypeSymbol>(), .. response.Select(p => p.Type)];
        bool readsResponse = responseTypes.Count > 0;

        int n = rawIndex++;
        string reqName = $"__Req{n}", resName = $"__Res{n}", serviceId = GetServiceId(method.GlobalNamespaced).ToString();
        var retry = GetRetry(method, false);

        if (request.Count > 0) EmitWriter(helpers, reqName, [.. request.Select(p => p.Type)], "");
        // Firma del contrato:
        if (readsResponse) EmitReader(helpers, resName, responseTypes, "", hasRet ? 1 : 0);

        string reqArgs = request.Count > 0 ? $"{reqName}({string.Join(", ", request.Select(p => p.Name))})" : "default";

        // GetCache descarta out/ref: con cache el unico resultado es el retorno.
        string? cacheKey = null;

        if (GetCache(method, false, contribution) is { } cache && GetCacheKey(method, [.. request.Select(p => (p, p.Type))], compilation, true, contribution) is { } key)
        {
            cacheKey = DeclareClientCache(helpers, n, cache, key, retType.GlobalNamespaced, reqArgs);
            // Clave por bytes = la peticion: en un fallo se envia tal cual, sin serializar dos veces.
            if (key.Bytes) reqArgs = "__cacheKey";
        }
        string outVars = string.Concat(response.Select((_, i) => $", out var __o{i}"));
        string outNames = string.Join(", ", response.Select((_, i) => $"__o{i}"));

        // Async: los out/ref viajan en la tupla de retorno (misma forma que la API tipada).
        string name = method.Name.EndsWith("Async") ? method.Name : method.Name + "Async";
        string asyncParams = string.Join(", ", method.Parameters.Where(p => p.RefKind is not RefKind.Out).Select(p => p.Type.GlobalNamespaced + " " + p.Name));

        code.Append("\n\n    public async global::System.Threading.Tasks.").Append(isTask && !isValueTask ? "Task" : "ValueTask")
            .Append(readsResponse ? "<" + TupleOf(responseTypes) + ">" : null).Append(' ').Append(name).Append('(').Append(asyncParams);

        if (tokenName is null)
            code.Append(asyncParams.Length > 0 ? ", " : null).Append(cancelTokenFullTypeName).Append(" @__token = default");

        string token = tokenName ?? "@__token";
        code.Append(")\n    {\n        ");
        int cacheStart = cacheKey != null ? OpenClientCache(code, n, cacheKey, token, true) : 0;
        EmitCall(code, retry, $"await {conn}.GetRawAsync({serviceId}, {reqArgs}, {token}).ConfigureAwait(false)", readsResponse ? "__res" : null, token, true);

        if (cacheKey != null)
            CloseClientCache(code.Append("\n\n        var __v = ").Append(resName).Append("(__res.Span);"), cacheStart, "__v");
        else if (!readsResponse) { }
        else if (response.Count == 0)
            code.Append("\n\n        return ").Append(resName).Append("(__res.Span);");
        else if (hasRet)
            code.Append("\n\n        return (").Append(resName).Append("(__res.Span").Append(outVars).Append("), ").Append(outNames).Append(");");
        else
            code.Append("\n\n        ").Append(resName).Append("(__res.Span").Append(outVars).Append(");\n\n        return ")
                .Append(response.Count == 1 ? outNames : "(" + outNames + ")").Append(';');

        code.Append("\n    }");

        // Contrato async sin token: la sobrecarga publica anade uno opcional y no lo implementa.
        if (isTask && tokenName is null)
            code.Append("\n\n    ").Append(method.ReturnType.GlobalNamespaced).Append(' ').Append(iFace.GlobalNamespaced).Append('.').Append(method.Name)
                .Append('(').Append(asyncParams).Append(") => ").Append(name).Append('(')
                .Append(string.Join(", ", method.Parameters.Select(p => p.Name))).Append(");");

        if (!isTask)
        {
            code.Append("\n\n    public ").Append(method.GlobalMemberSignature).Append("\n    {\n        ");
            int syncCacheStart = cacheKey != null ? OpenClientCache(code, n, cacheKey, tokenName, false) : 0;
            EmitCall(code, retry, $"{conn}.GetRaw({serviceId}, {reqArgs}" + (tokenName is null ? "" : $", {tokenName}") + ")", readsResponse ? "__res" : null, tokenName, false);

            if (cacheKey != null)
                CloseClientCache(code.Append("\n\n        var __v = ").Append(resName).Append("(__res.Span);"), syncCacheStart, "__v");
            else if (readsResponse)
                code.Append("\n\n        ").Append(hasRet ? "return " : null).Append(resName).Append("(__res.Span").Append(string.Concat(response.Select(p => ", out " + p.Name))).Append(");");

            code.Append("\n    }");
        }

        return true;
    }
}
