using Microsoft.CodeAnalysis;
using SourceCrafter.DependencyInjection.Generation;
using SourceCrafter.LiteSpeedLink.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

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
    /// Lector estatico: <c>ReadOnlySpan&lt;byte&gt;</c> -> valor o tupla (orden del cable: retorno, luego out/ref), con los metodos
    /// especificos de cada tipo. El transporte lo invoca sobre su propio buffer: la respuesta nunca se copia.
    /// </summary>
    private static void EmitReader(CodeBuilder code, string name, IReadOnlyList<ITypeSymbol> types)
    {
        code.Append("\n\nprivate static ").Append(TupleOf(types)).Append(' ').Append(name).Append("(global::System.ReadOnlySpan<byte> __b)")
            .Open()
            .Line("using var __s = global::MemoryPack.MemoryPackReaderOptionalStatePool.Rent(null);")
            .Line("var __r = new global::MemoryPack.MemoryPackReader(__b, __s);")
            .Line("return ").Append(types.Count == 1 ? ReadCall(types[0]) : "(" + string.Join(", ", types.Select(ReadCall)) + ")").Append(';')
            .Close();
    }

    /// <summary>Argumento lector de <c>GetRaw(Async)</c>: lambda <c>static</c> (cacheada) sobre el lector generado, o descarte sin respuesta.</summary>
    private static string RawReaderArg(string? reader) => reader is null ? "static _ => true" : "static __b => " + reader + "(__b)";

    /// <summary>Peticion ya materializada (clave de cache por bytes): se escribe tal cual en la trama.</summary>
    private const string BytesWriter = "static (__rw, __rs) => global::System.Buffers.BuffersExtensions.Write(__rw, __rs.Span)";

    /// <summary>Peticion sin argumentos: cuerpo vacio.</summary>
    private const string NoRequest = "(byte)0, static (_, _) => { }";

    /// <summary>
    /// Reparte la respuesta <c>__res</c> (valor o tupla) en <paramref name="targets"/>, uno por tipo del cable
    /// (p. ej. <c>var __r</c>, un parametro out/ref o <c>var __o0</c>).
    /// </summary>
    private static void Unpack(CodeBuilder code, IReadOnlyList<string> targets)
    {
        for (int i = 0; i < targets.Count; i++)
            code.Line(targets[i]).Append(" = __res").Append(targets.Count == 1 ? null : ".Item" + (i + 1)).Append(';');
    }

    /// <summary>
    /// Escritor estatico: argumentos -> bytes de la peticion (cuerpo sin cabecera). Con <paramref name="direct"/> escribe en el
    /// <c>IBufferWriter</c> del transporte (su trama), sin buffer intermedio ni copia; si no, materializa (claves de cache por bytes).
    /// </summary>
    private static void EmitWriter(CodeBuilder code, string name, IReadOnlyList<ITypeSymbol> types, bool direct = false)
    {
        string parameters = string.Join(", ", types.Select((t, i) => t.GlobalNamespaced + " __p" + i));

        if (direct)
        {
            code.Append("\n\nprivate static void ").Append(name).Append("(global::System.Buffers.IBufferWriter<byte> __out, ").Append(parameters).Append(')')
                .Open()
                .Line("using var __s = global::MemoryPack.MemoryPackWriterOptionalStatePool.Rent(null);")
                .Line("var __w = new global::MemoryPack.MemoryPackWriter<global::System.Buffers.IBufferWriter<byte>>(ref __out, __s);");

            for (int i = 0; i < types.Count; i++)
                code.Line(WriteCall(types[i], "__p" + i));

            code.Line("__w.Flush();").Close();
            return;
        }

        code.Append("\n\nprivate static global::System.ReadOnlyMemory<byte> ").Append(name).Append('(').Append(parameters).Append(')')
            .Open()
            // ponytail: un ArrayBufferWriter por llamada; solo para bytes que deben sobrevivir a la llamada (claves de cache).
            // Primitivos exactos; strings con cota UTF-8 (cabecera 8 + 3/char, sin recorrer el string: nunca crece); packables/valores parten de 64 y crecen.
            .Line("var __buf = new global::System.Buffers.ArrayBufferWriter<byte>(").Append(CapacityOf(types)).Append(");")
            .Open("using (var __s = global::MemoryPack.MemoryPackWriterOptionalStatePool.Rent(null))")
            .Line("var __w = new global::MemoryPack.MemoryPackWriter<global::System.Buffers.ArrayBufferWriter<byte>>(ref __buf, __s);");

        for (int i = 0; i < types.Count; i++)
            code.Line(WriteCall(types[i], "__p" + i));

        code.Line("__w.Flush();").Close()
            .Line("return __buf.WrittenMemory;").Close();
    }

    /// <summary>
    /// Argumentos de <c>GetRaw(Async)</c> para un escritor directo: estado (valor o tupla, sin box) y lambda <c>static</c>
    /// (delegado cacheado por el compilador) que lo desempaqueta hacia <paramref name="writer"/>.
    /// </summary>
    private static string DirectRequest(string writer, IReadOnlyList<string> args) =>
        (args.Count == 1 ? args[0] : "(" + string.Join(", ", args) + ")")
        + ", static (__rw, __rs) => " + writer + "(__rw, "
        + (args.Count == 1 ? "__rs" : string.Join(", ", args.Select((_, i) => "__rs.Item" + (i + 1)))) + ")";

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

            if (method.GetAttributes().Any(a => IsMiddlewareAttribute(a, false) || IsMiddlewareAttribute(a, true)))
                contribution.ReportDiagnostic(Diagnostic.Create(MiddlewareOnStream, location, method.Name));
        }
        else if (GetRetry(method, false) is { } c && GetRetry(method, true) is { } s)
            contribution.ReportDiagnostic(Diagnostic.Create(RetryOnBothSides, location, method.Name, c.Attempts * s.Attempts));
    }

    /// <summary>
    /// Sentencia de llamada al transporte (<paramref name="call"/>), con la respuesta en <paramref name="target"/> si no es null.
    /// Con [ClientRetry]/[ServerRetry] va en un bucle inline que repite solo esa llamada ante TimeoutException, nunca si el
    /// llamante cancelo: sin structs ni delegados por operacion; processors y lectores quedan fuera y se ejecutan una vez.
    /// </summary>
    /// Con <paramref name="ret"/> y sin reintento, el resultado va directo a <c>return</c> (devuelve true si lo hizo).
    private static bool EmitCall(CodeBuilder code, (int Attempts, int IntervalMs)? retry, string call, string? target, string? token, bool isAsync,
        string targetType, IReadOnlyList<string>? middlewares = null, IReadOnlyList<(string Expr, string Name)>? captures = null,
        IReadOnlyList<(string Name, string Type, bool IsOut)>? outs = null, bool ret = false)
    {
        if (middlewares is { Count: > 0 })
            return EmitMiddlewares(code, middlewares, captures ?? [], outs ?? [], retry, call, target, token, isAsync, targetType, ret);

        if (retry is not { } r)
        {
            code.Append(target is null ? null : ret ? "return " : "var " + target + " = ").Append(call).Append(';');
            return ret && target != null;
        }

        if (target != null) code.Append(targetType).Append(' ').Append(target).Append(';').Blank();

        code.Open("for (var __attempt = 1; ; __attempt++)")
            .Line("try { ").Append(target is null ? null : target + " = ").Append(call).Append("; break; }")
            .Line("catch (global::System.TimeoutException) when (__attempt < ").Append(r.Attempts)
            .Append(token is null ? null : " && !" + token + ".IsCancellationRequested").Append(") { }");

        if (r.IntervalMs > 0)
            code.Blank().Append(isAsync
                ? $"await global::System.Threading.Tasks.Task.Delay({r.IntervalMs}, {token}).ConfigureAwait(false);"
                : token is null
                    ? $"global::System.Threading.Thread.Sleep({r.IntervalMs});"
                    // GetResult y no Wait(): la cancelacion sale como TaskCanceledException, igual que en async.
                    : $"global::System.Threading.Tasks.Task.Delay({r.IntervalMs}, {token}).GetAwaiter().GetResult();");

        code.Close();
        return false;
    }

    /// <summary>
    /// [Client|ServerMiddleware]: la llamada (con su reintento) va en la lambda <c>static</c> mas interna y cada middleware la
    /// envuelve con otra lambda <c>static</c>: delegados cacheados por el compilador, sin structs por capa. Todas las capas
    /// comparten un unico estado (middlewares internos + <paramref name="captures"/>); el primero es el mas externo.
    /// Async: solo la capa interna tiene maquina de estados (pooled); las de paso devuelven el ValueTask del siguiente.
    /// <paramref name="outs"/> (host): out/ref vuelven en el resultado; out se declara, ref se reasigna.
    /// </summary>
    private static bool EmitMiddlewares(CodeBuilder code, IReadOnlyList<string> mws, IReadOnlyList<(string Expr, string Name)> captures,
        IReadOnlyList<(string Name, string Type, bool IsOut)> outs, (int Attempts, int IntervalMs)? retry, string call, string? target, string? token,
        bool isAsync, string targetType, bool ret)
    {
        // Sync con token: la lambda no lo recibe, viaja en el estado.
        if (!isAsync && token != null) captures = [.. captures, (token, token)];

        // Solo viaja lo que la llamada usa (y el token si el reintento lo consulta).
        static string Ident(string name) => @"(?<![\w.@:])" + Regex.Escape(name) + @"(?![\w:])";
        captures = [.. captures.Where(c => Regex.IsMatch(call, Ident(c.Name)) || retry != null && c.Name == token)];

        List<string> state = [.. mws.Skip(1), .. captures.Select(c => c.Expr)];
        string Item(int i) => state.Count == 1 ? "__s" : "__s.Item" + (i + 1);
        string Field(string name) => state.Count == 1 ? "__s" : "__s." + name;
        string tuple = state.Count switch
        {
            0 => "(byte)0",
            1 => state[0],
            _ => "(" + string.Join(", ", mws.Skip(1).Concat(captures.Select(c => c.Expr == c.Name ? c.Expr : c.Name + ": " + c.Expr))) + ")"
        };

        List<(string Name, string Type)> results = [.. target is null ? [] : new[] { (target!, targetType) }, .. outs.Select(o => (o.Name, o.Type))];
        string resType = results.Count switch { 0 => "bool", 1 => results[0].Type, _ => "(" + string.Join(", ", results.Select(r => r.Type)) + ")" };

        string head = isAsync ? "static global::System.Threading.Tasks.ValueTask<" + resType + "> (__s, " + token + ")" : "static " + resType + " (__s)";
        string Next = isAsync ? ".InvokeAsync(" : ".Invoke(";

        // Un unico resultado sin reintento ni out/ref: lambda de expresion; si no, bloque con la sentencia de EmitCall.
        bool expr = outs.Count == 0 && retry is null && target != null, awaits;
        var inner = new CodeBuilder();

        if (outs.Count > 0)
        {
            // out/ref: locales que se pasan por referencia y vuelven en el resultado.
            if (captures.Count > 0)
                inner.Append(state.Count == 1 ? "var " + captures[0].Name + " = __s;"
                    : "var (" + string.Join(", ", state.Select((_, i) => i < mws.Count - 1 ? "_" : captures[i - (mws.Count - 1)].Name)) + ") = __s;")
                    .Append('\n');

            foreach (var o in outs.Where(o => o.IsOut)) inner.Append(o.Type).Append(' ').Append(o.Name).Append(";\n");

            call = call.Replace("out var ", "out ");
        }
        else
            // Cada elemento del estado se lee una vez: acceso directo, sin deconstruir.
            foreach (var c in captures) call = Regex.Replace(call, Ident(c.Name), Field(c.Name));

        // Solo GetRawAsync: la lambda devuelve su ValueTask tal cual, sin async ni maquina de estados.
        string? direct = expr && isAsync ? DirectRaw(call) : null;
        if (direct != null)
            (call, awaits) = (direct, false);
        else if (expr)
            awaits = isAsync && call.Contains("await ");
        else
        {
            EmitCall(inner, retry, call, target, isAsync || token is null || outs.Count > 0 ? token : Field(token), isAsync, targetType);
            string result = results.Count switch
            {
                0 => "true",
                1 => results[0].Name,
                _ => "(" + string.Join(", ", results.Select(r => r.Name)) + ")"
            };
            // Async con maquina de estados (pooled) solo si la capa interna espera; si no, devuelve el ValueTask ya completado.
            awaits = isAsync && inner.ToString().Contains("await ");
            inner.Append("\n\nreturn ").Append(isAsync && !awaits ? "new global::System.Threading.Tasks.ValueTask<" + resType + ">(" + result + ")" : result).Append(';');
        }

        var assigned = results.Select((r, i) => (i == 0 && target != null) || outs[i - (target is null ? 0 : 1)].IsOut ? "var " + r.Name : r.Name).ToList();
        bool returns = ret && results.Count == 1 && target != null;

        // Relativa a la sentencia: al anidarla, la profundidad de code la sangra.
        var lambda = new CodeBuilder(1);

        code.Append(assigned.Count switch { 0 => null, 1 => returns ? "return " : assigned[0] + " = ", _ => "(" + string.Join(", ", assigned) + ") = " })
            .Append(isAsync ? "await " : null).Append(mws[0]).Append(Next).Append(tuple).Append(',');

        for (int layer = 1; layer < mws.Count; layer++)
            lambda.Line(head).Indent().Line("=> ").Append(Item(layer - 1)).Append(Next).Append("__s,").Indent();

        if (awaits) lambda.Line(PooledBuilder(true)).Line("static async global::System.Threading.Tasks.ValueTask<" + resType + "> (__s, " + token + ")");
        else lambda.Line(head);

        if (expr) lambda.Indent().Line("=> ").Append(isAsync && !awaits && direct is null ? "new global::System.Threading.Tasks.ValueTask<" + resType + ">(" + call + ")" : call).Unindent();
        else lambda.Append(" =>").Open().Append(inner).Close();

        for (int layer = 0; layer < mws.Count; layer++)
        {
            if (layer > 0) lambda.Unindent().Unindent();
            lambda.Append(isAsync ? "," : null).Line(isAsync ? token + ")" : ")");
        }

        // Sync: el cierre va pegado a la lambda.
        string text = lambda.ToString();
        if (!isAsync) text = Regex.Replace(text, @"\n\s*\)", ")");

        code.Append('\n').Append(text).Append(isAsync ? ".ConfigureAwait(false);" : ";");
        return returns;
    }

    /// <summary>
    /// Caja async reutilizada en los metodos ValueTask generados (cada llamada creaba la suya). El ValueTask resultante se
    /// consume una sola vez, que es la regla de ValueTask; los contratos Task no pueden usarlo.
    /// </summary>
    private static string PooledBuilder(bool generic) =>
        "[global::System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(global::System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder"
        + (generic ? "<>" : null) + "))]";

    /// <summary><c>await X.GetRawAsync(...).ConfigureAwait(false)</c> sin mas esperas -> la llamada sin await (su ValueTask); si no, null.</summary>
    private static string? DirectRaw(string call) =>
        call.StartsWith("await ") && call.LastIndexOf("await ") == 0 && call.Contains(".GetRawAsync(") && call.EndsWith(".ConfigureAwait(false)")
            ? call.Substring(6, call.Length - 6 - ".ConfigureAwait(false)".Length)
            : null;

    /// <summary>
    /// Cliente unario raw:
    /// <c>GetRaw</c>/<c>GetRawAsync</c>; la respuesta se lee con lectores especificos. Streams y operaciones
    /// sin retorno se envian igual; el cuerpo de respuesta se ignora salvo out/ref. Streams y Task sin
    /// resultado siguen por la API tipada.
    /// </summary>
    private static bool TryGenerateRawClientMethod(CodeBuilder code, CodeBuilder helpers, INamedTypeSymbol iFace, IMethodSymbol method, string conn, ref int rawIndex,
        ServiceProviderInfo container, PartialContribution contribution)
    {
        var compilation = container.Compilation;
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

        if (readsResponse) EmitReader(helpers, resName, responseTypes);

        List<string> reqNames = [.. request.Select(p => p.Name)];
        string reqArgs = request.Count > 0 ? DirectRequest(reqName, reqNames) : NoRequest;
        bool materialize = false;

        // GetCache descarta out/ref: con cache el unico resultado es el retorno.
        string? cacheKey = null;

        if (GetCache(method, false, contribution) is { } cache && GetCacheKey(method, [.. request.Select(p => (p, p.Type))], compilation, true, contribution) is { } key)
        {
            cacheKey = DeclareClientCache(helpers, n, cache, key, retType.GlobalNamespaced, request.Count > 0 ? $"{reqName}({string.Join(", ", reqNames)})" : "default");
            // Clave por bytes = la peticion: en un fallo se envia tal cual, sin serializar dos veces.
            if (key.Bytes) (reqArgs, materialize) = ("__cacheKey, " + BytesWriter, true);
        }

        if (request.Count > 0) EmitWriter(helpers, reqName, [.. request.Select(p => p.Type)], direct: !materialize);
        string reader = RawReaderArg(readsResponse ? resName : null), resType = TupleOf(responseTypes);
        var mws = GetMiddlewares(method, false, container, contribution);
        List<(string, string)> captures = [("__connection", "__connection"), .. reqNames.Select(r => (r, r)), .. materialize ? [("__cacheKey", "__cacheKey")] : Enumerable.Empty<(string, string)>()];

        // Async: los out/ref viajan en la tupla de retorno (misma forma que la API tipada).
        string name = method.Name.EndsWith("Async") ? method.Name : method.Name + "Async";
        string asyncParams = string.Join(", ", method.Parameters.Where(p => p.RefKind is not RefKind.Out).Select(p => p.Type.GlobalNamespaced + " " + p.Name));

        bool valueTask = !isTask || isValueTask;
        var asyncChain = MiddlewareChain(mws, true, "__provider.", method, contribution);
        // Solo devuelve GetRawAsync: su ValueTask sale tal cual, sin async ni maquina de estados.
        bool direct = valueTask && readsResponse && retry is null && cacheKey is null && asyncChain is not { Count: > 0 };
        // Los miembros del cliente van un nivel dentro de su clase.
        code.Indent().Append("\n\n");
        if (valueTask && !direct) code.Append(PooledBuilder(readsResponse)).Append('\n');
        code.Append(direct ? "public global::System.Threading.Tasks." : "public async global::System.Threading.Tasks.").Append(valueTask ? "ValueTask" : "Task")
            .Append(readsResponse ? "<" + TupleOf(responseTypes) + ">" : null).Append(' ').Append(name).Append('(').Append(asyncParams);

        if (tokenName is null)
            code.Append(asyncParams.Length > 0 ? ", " : null).Append(cancelTokenFullTypeName).Append(" @__token = default");

        string token = tokenName ?? "@__token";
        code.Append(')').Open();
        if (cacheKey != null) OpenClientCache(code, n, cacheKey, token, true);
        string rawCall = $"{conn}.GetRawAsync({serviceId}, {reqArgs}, {reader}, {token})";
        bool returned = EmitCall(code, retry, direct ? rawCall : $"await {rawCall}.ConfigureAwait(false)", readsResponse ? "__res" : null, token, true, resType,
            asyncChain, captures, ret: cacheKey is null);

        // Async: la respuesta (valor o tupla retorno + out/ref) ya tiene la forma del retorno.
        if (cacheKey != null) CloseClientCache(code, "__res");
        else if (readsResponse && !returned) code.Blank().Line("return __res;");

        code.Close();

        // Contrato async sin token: la sobrecarga publica anade uno opcional y no lo implementa.
        if (isTask && tokenName is null)
            code.Append("\n\n").Append(method.ReturnType.GlobalNamespaced).Append(' ').Append(iFace.GlobalNamespaced).Append('.').Append(method.Name)
                .Append('(').Append(asyncParams).Append(") => ").Append(name).Append('(')
                .Append(string.Join(", ", method.Parameters.Select(p => p.Name))).Append(");");

        if (!isTask)
        {
            code.Append("\n\npublic ").Append(method.GlobalMemberSignature).Open();
            if (cacheKey != null) OpenClientCache(code, n, cacheKey, tokenName, false);
            returned = EmitCall(code, retry, $"{conn}.GetRaw({serviceId}, {reqArgs}, {reader}" + (tokenName is null ? "" : $", {tokenName}") + ")", readsResponse ? "__res" : null, tokenName, false, resType,
                MiddlewareChain(mws, false, "__provider.", method, contribution), captures, ret: cacheKey is null && response.Count == 0);

            if (cacheKey != null) CloseClientCache(code, "__res");
            else if (response.Count == 0) { if (hasRet && !returned) code.Blank().Line("return __res;"); }
            else
            {
                // Sync: out/ref salen a los parametros del llamador.
                Unpack(code.Blank(), [.. hasRet ? ["var __v"] : Enumerable.Empty<string>(), .. response.Select(p => p.Name)]);
                if (hasRet) code.Blank().Line("return __v;");
            }

            code.Close();
        }

        code.Unindent();
        return true;
    }
}
