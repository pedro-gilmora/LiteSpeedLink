using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SourceCrafter.DependencyInjection.Generation;
using SourceCrafter.LiteSpeedLink.Helpers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

public partial class ServiceHandlersGenerator
{
    private const string CacheKeyAttr = "SourceCrafter.LiteSpeedLink.CacheKeyAttribute";

    private static readonly DiagnosticDescriptor CacheKeyBytes = new("SCLSL018", "Cache key falls back to bytes",
        "'{0}': parameter '{1}' has no value equality, so the [ClientCache] key is its serialized bytes (raise SCLSL018 to error to require value equality)",
        "LiteSpeedLink", DiagnosticSeverity.Info, true);

    private static readonly DiagnosticDescriptor CacheKeyInvalid = new("SCLSL019", "Invalid cache key",
        "'{0}': invalid [CacheKey]: {1}", "LiteSpeedLink", DiagnosticSeverity.Error, true);

    /// <summary>
    /// Clave de cache decidida en compilacion. <see cref="Leaves"/>: expresion sobre los argumentos y su tipo (anulable si la
    /// ruta pasa por <c>?.</c>). <see cref="Explicit"/>: vino de [CacheKey]. <see cref="Bytes"/>: sin [CacheKey] y algun
    /// parametro sin igualdad por valor, la clave son sus bytes serializados.
    /// </summary>
    private sealed record KeyPlan(List<(string Expr, ITypeSymbol Type)> Leaves, bool Explicit, bool Bytes)
    {
        // Una hoja de tipo valor no anulable va tal cual; anulable o referencia va en ValueTuple (el diccionario no admite null).
        private bool Bare => Leaves is [{ Type: { IsValueType: true } t }] && t.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T;

        public string Type => Bytes ? "global::System.ReadOnlyMemory<byte>"
            : Leaves.Count == 0 ? "byte"
            : Bare ? Leaves[0].Type.GlobalNamespaced
            : Leaves.Count == 1 ? "global::System.ValueTuple<" + Leaves[0].Type.GlobalNamespaced + ">"
            : "(" + string.Join(", ", Leaves.Select(l => l.Type.GlobalNamespaced)) + ")";

        // ponytail: sin parametros de clave, un manager con clave constante en lugar de un campo Entry? propio.
        public string Expr => Leaves.Count == 0 ? "(byte)0"
            : Bare ? Leaves[0].Expr
            : Leaves.Count == 1 ? "new global::System.ValueTuple<" + Leaves[0].Type.GlobalNamespaced + ">(" + Leaves[0].Expr + ")"
            : "(" + string.Join(", ", Leaves.Select(l => l.Expr)) + ")";
    }

    private static AttributeData? CacheKeyOf(ImmutableArray<AttributeData> attrs) =>
        attrs.FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == CacheKeyAttr);

    /// <summary>
    /// Clave sobre <paramref name="roots"/> (parametros de payload con el tipo que ve este extremo); null si [CacheKey] no es
    /// valido (SCLSL019) y el metodo no se cachea. SCLSL018 solo con <paramref name="reportBytes"/>: el servidor sin
    /// [CacheKey] siempre usa los bytes de la peticion.
    /// </summary>
    private static KeyPlan? GetCacheKey(IMethodSymbol method, List<(IParameterSymbol Param, ITypeSymbol Type)> roots,
        Compilation compilation, bool reportBytes, PartialContribution contribution)
    {
        var location = method.Locations.FirstOrDefault();
        var onMethod = CacheKeyOf(method.GetAttributes());
        var marked = roots.Where(r => CacheKeyOf(r.Param.GetAttributes()) != null).ToList();

        if (onMethod is null && marked.Count == 0)
        {
            var lacking = roots.Where(r => !HasValueEquality(r.Type)).ToList();

            if (reportBytes)
                foreach (var r in lacking)
                    contribution.ReportDiagnostic(Diagnostic.Create(CacheKeyBytes, location, method.Name, r.Param.Name));

            return new([.. roots.Select(r => (r.Param.Name, r.Type))], false, lacking.Count > 0);
        }

        List<(string Expr, ITypeSymbol Type)> leaves = [];

        string? error = onMethod != null && marked.Count > 0 ? "it's on the method and on its parameters at once"
            : onMethod != null ? AddPaths(onMethod, roots, true)
            : null;

        foreach (var r in marked)
            if ((error ??= AddPaths(CacheKeyOf(r.Param.GetAttributes())!, [r], false)) != null) break;

        error ??= leaves.Where(l => !HasValueEquality(l.Type)).Select(l => $"'{l.Expr}' ({l.Type.ToDisplayString()}) has no value equality").FirstOrDefault();

        if (error is null) return new(leaves, true, false);

        contribution.ReportDiagnostic(Diagnostic.Create(CacheKeyInvalid, location, method.Name, error));
        return null;

        string? AddPaths(AttributeData attr, List<(IParameterSymbol Param, ITypeSymbol Type)> scope, bool rooted)
        {
            var paths = CacheKeyPaths(attr);

            // Sin miembros: el parametro entero (o, en el metodo, todos).
            if (paths.Count == 0) leaves.AddRange(scope.Select(r => (r.Param.Name, r.Type)));

            foreach (var (path, isNameof) in paths)
            {
                if (ResolveKeyPath(path, isNameof, scope, rooted, compilation, out var leaf) is { } why) return why;
                leaves.Add(leaf);
            }

            return null;
        }
    }

    /// <summary>
    /// Rutas de [CacheKey]. <c>nameof(a.b.c)</c> evalua a <c>"c"</c>: con sintaxis (contrato en este proyecto) se toma la
    /// ruta completa del <c>nameof</c>; sin ella (otro ensamblado) solo queda el valor ya plegado.
    /// </summary>
    private static List<(string Path, bool Nameof)> CacheKeyPaths(AttributeData attr)
    {
        if (attr.ConstructorArguments is not [{ Kind: TypedConstantKind.Array } members]) return [];

        var values = members.Values.Select(v => v.Value as string ?? "").ToList();

        var exprs = (attr.ApplicationSyntaxReference?.GetSyntax() as AttributeSyntax)?.ArgumentList?.Arguments
            .SelectMany(a => a.Expression switch
            {
                ImplicitArrayCreationExpressionSyntax i => i.Initializer.Expressions,
                ArrayCreationExpressionSyntax { Initializer: { } i } => i.Expressions,
                CollectionExpressionSyntax c => c.Elements.OfType<ExpressionElementSyntax>().Select(e => e.Expression),
                var e => (IEnumerable<ExpressionSyntax>)[e]
            })
            .ToList();

        return [.. values.Select((v, i) => exprs?.Count == values.Count
                && exprs[i] is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" }, ArgumentList.Arguments: [{ } arg] }
            ? (string.Concat(arg.Expression.ToString().Where(c => !char.IsWhiteSpace(c))), true)
            : (v, false))];
    }

    /// <summary>
    /// Resuelve una ruta (ordinal). En el metodo la raiz es el nombre del parametro; en un parametro, su tipo
    /// (<c>nameof(Tipo.A.B)</c> arrastra el tipo y su espacio de nombres: se descartan prefijos, el compilador ya valido la ruta).
    /// Un solo segmento que no resuelve (nameof anidado plegado) se busca en el grafo de miembros.
    /// </summary>
    private static string? ResolveKeyPath(string path, bool isNameof, List<(IParameterSymbol Param, ITypeSymbol Type)> scope, bool rooted,
        Compilation compilation, out (string Expr, ITypeSymbol Type) leaf)
    {
        var segments = path.Split('.');
        leaf = default;

        foreach (var r in scope)
            for (int skip = rooted ? 1 : 0, last = rooted ? 1 : isNameof ? segments.Length - 1 : 0; skip <= last; skip++)
                if ((!rooted || r.Param.Name == segments[0]) && WalkKeyPath(r.Param.Name, r.Type, segments.Skip(skip), compilation) is { } found)
                {
                    leaf = found;
                    return null;
                }

        if (segments.Length > 1) return $"'{path}' is not a path of public instance properties/fields";

        var matches = FindKeyMember(path, scope);

        if (matches.Count != 1)
            return matches.Count == 0 ? $"'{path}' not found"
                : $"'{path}' is ambiguous ({string.Join(", ", matches.Select(m => m.Root.Param.Name + "." + string.Join(".", m.Path)))}): use the literal path";

        leaf = WalkKeyPath(matches[0].Root.Param.Name, matches[0].Root.Type, matches[0].Path, compilation)!.Value;
        return null;
    }

    /// <summary>Expresion de la ruta; cada tramo de tipo referencia con <c>?.</c> y, si hubo alguno, hoja anulable.</summary>
    private static (string Expr, ITypeSymbol Type)? WalkKeyPath(string root, ITypeSymbol type, IEnumerable<string> segments, Compilation compilation)
    {
        var expr = root;
        bool lifted = false;

        foreach (var s in segments)
        {
            // ponytail: no recorre Nullable<T> (exigiria .Value); ampliar si un contrato lo necesita.
            if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T || KeyMember(type, s) is not { } member) return null;

            expr += (type.IsReferenceType ? "?." : ".") + s;
            lifted |= type.IsReferenceType;
            type = member;
        }

        if (lifted && type.IsValueType && type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T)
            type = compilation.GetSpecialType(SpecialType.System_Nullable_T).Construct(type);
        else if (lifted && type.IsReferenceType)
            type = type.WithNullableAnnotation(NullableAnnotation.Annotated);

        return (expr, type);
    }

    /// <summary>Propiedad (sin indexador, con get) o campo publico de instancia; ni metodos ni indexadores.</summary>
    private static ITypeSymbol? KeyMember(ITypeSymbol type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
            foreach (var m in t.GetMembers(name))
                if (m is { IsStatic: false, DeclaredAccessibility: Accessibility.Public })
                    switch (m)
                    {
                        case IPropertySymbol { IsIndexer: false, GetMethod: not null } p: return p.Type;
                        case IFieldSymbol f: return f.Type;
                    }

        return null;
    }

    // ponytail: busqueda en anchura de 4 niveles que no entra en primitivos, enums ni colecciones; ampliar si un contrato lo pide.
    private static List<((IParameterSymbol Param, ITypeSymbol Type) Root, List<string> Path)> FindKeyMember(string name, List<(IParameterSymbol Param, ITypeSymbol Type)> scope)
    {
        List<((IParameterSymbol, ITypeSymbol) Root, List<string> Path)> found = [];
        var queue = new Queue<((IParameterSymbol, ITypeSymbol) Root, ITypeSymbol Type, List<string> Path)>(scope.Select(r => (r, r.Type, new List<string>())));

        while (queue.Count > 0)
        {
            var (root, type, path) = queue.Dequeue();

            if (path.Count >= 4 || type.SpecialType != SpecialType.None || type.TypeKind == TypeKind.Enum
                || type is IArrayTypeSymbol || type.AllInterfaces.Any(i => i.ToDisplayString() == "System.Collections.IEnumerable"))
                continue;

            for (var t = type; t != null; t = t.BaseType)
                foreach (var m in t.GetMembers())
                    if (m is { IsStatic: false, DeclaredAccessibility: Accessibility.Public } && KeyMember(type, m.Name) is { } memberType && m is IPropertySymbol or IFieldSymbol)
                    {
                        List<string> next = [.. path, m.Name];

                        if (m.Name == name) found.Add((root, next));

                        queue.Enqueue((root, memberType, next));
                    }
        }

        return found;
    }

    private static bool HasValueEquality(ITypeSymbol type) => HasValueEquality(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));

    /// <summary>
    /// Primitivo, string, enum, Nullable/ValueTuple de ellos, record cuyos campos la tienen, o tipo que declara
    /// <c>IEquatable&lt;T&gt;</c> (contrato del desarrollador). Clases sin ella, arrays y colecciones comparan referencia.
    /// </summary>
    private static bool HasValueEquality(ITypeSymbol type, HashSet<ITypeSymbol> seen)
    {
        if (type.TypeKind == TypeKind.Enum
            || type.SpecialType is >= SpecialType.System_Boolean and <= SpecialType.System_String or SpecialType.System_IntPtr or SpecialType.System_UIntPtr or SpecialType.System_DateTime)
            return true;

        // Arrays, punteros y parametros de tipo: igualdad por referencia o desconocida.
        if (type is not INamedTypeSymbol named) return false;

        // Ciclo: lo decide la primera visita.
        if (!seen.Add(named)) return true;

        if (named.IsTupleType) return named.TupleElements.All(e => HasValueEquality(e.Type, seen));

        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) return HasValueEquality(named.TypeArguments[0], seen);

        if (!named.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == "System.IEquatable<T>" && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], named)))
            return false;

        // El Equals de un record compara todos sus campos, tambien los de los records base.
        for (var t = named; t is { IsRecord: true }; t = t.BaseType)
            if (!t.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).All(f => HasValueEquality(f.Type, seen)))
                return false;

        return true;
    }

    /// <summary>SCLSL017 de [CacheKey]: sobre el CancellationToken, o en un metodo sin [ClientCache]/[ServerCache].</summary>
    private static void ReportCacheKeyIgnored(IMethodSymbol method, PartialContribution contribution)
    {
        foreach (var p in method.Parameters)
            if (CacheKeyOf(p.GetAttributes()) is { } attr && p.Type.GlobalNamespaced == cancelTokenFullTypeName)
                contribution.ReportDiagnostic(Diagnostic.Create(CacheIgnored, attr.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? method.Locations.FirstOrDefault(),
                    method.Name, "CacheKey", "the CancellationToken is never part of the key"));

        if ((CacheKeyOf(method.GetAttributes()) ?? method.Parameters.Select(p => CacheKeyOf(p.GetAttributes())).FirstOrDefault(a => a != null)) is { } any
            && !method.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() is ClientCacheAttr or ServerCacheAttr))
            contribution.ReportDiagnostic(Diagnostic.Create(CacheIgnored, any.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? method.Locations.FirstOrDefault(),
                method.Name, "CacheKey", "the method has no [ClientCache]/[ServerCache]"));
    }

    /// <summary>
    /// [ClientCache] por instancia de cliente, creado al primer uso. Devuelve la expresion de la clave; con alternativa por
    /// bytes y sin processors es la propia peticion (<paramref name="request"/>), que en un fallo se envia tal cual.
    /// </summary>
    private static string DeclareClientCache(StringBuilder helpers, int n, (int DurationMs, int Capacity) cache, KeyPlan key, string valueType, string? request)
    {
        var manager = "global::SourceCrafter.LiteSpeedLink.CacheManager<" + key.Type + ", " + valueType + ">";

        helpers.Append("\n\nprivate ").Append(manager).Append(" __cache").Append(n).Append(";\n\nprivate ").Append(manager).Append(" __Cache").Append(n)
            .Append(" =>\n    global::System.Threading.LazyInitializer.EnsureInitialized(ref __cache").Append(n).Append(", static () => new ").Append(manager)
            .Append('(').Append(cache.DurationMs).Append(", ").Append(cache.Capacity)
            .Append(key.Bytes ? ", global::SourceCrafter.LiteSpeedLink.ByteContentComparer.Instance" : null).Append("));");

        if (!key.Bytes) return key.Expr;

        if (request != null) return request;

        EmitWriter(helpers, "__Key" + n, [.. key.Leaves.Select(l => l.Type)], "");
        return "__Key" + n + "(" + string.Join(", ", key.Leaves.Select(l => l.Expr)) + ")";
    }

    /// <summary>
    /// Acierto -> resultado; en curso -> espera al lider (su cancelacion solo cancela la espera); si no, lidera.
    /// Devuelve el inicio del cuerpo del try, que <see cref="CloseClientCache"/> indenta.
    /// </summary>
    private static int OpenClientCache(StringBuilder code, int n, string key, string? token, bool isAsync)
    {
        code.Append("var __cache = __Cache").Append(n).Append(";\n        var __cacheKey = ").Append(key)
            .Append(";\n\n        if (__cache.TryGet(__cacheKey, out var __cached)) return __cached;\n\n        if (!__cache.TryLead(__cacheKey, out var __flight))\n            return ")
            .Append(isAsync ? "await __flight.Task.WaitAsync(" + token + ").ConfigureAwait(false)"
                : token is null ? "__flight.Task.GetAwaiter().GetResult()"
                : "__flight.Task.WaitAsync(" + token + ").GetAwaiter().GetResult()")
            .Append(";\n\n        try\n        {");

        int start = code.Length;
        code.Append("\n        ");
        return start;
    }

    /// <summary>Publica el resultado final; excepcion, rechazo o cancelacion no se cachean y llegan a los que esperaban.</summary>
    private static void CloseClientCache(StringBuilder code, int start, string value)
    {
        while (char.IsWhiteSpace(code[code.Length - 1])) code.Length--;

        code.Append("\n\n        __cache.Complete(__cacheKey, __flight, ").Append(value).Append(");\n        return ").Append(value).Append(';')
            .Replace("\n        ", "\n            ", start, code.Length - start)
            .Append("\n        }\n        catch (global::System.Exception __cacheError)\n        {\n            __cache.Fail(__cacheKey, __flight, __cacheError);\n            throw;\n        }");
    }
}
