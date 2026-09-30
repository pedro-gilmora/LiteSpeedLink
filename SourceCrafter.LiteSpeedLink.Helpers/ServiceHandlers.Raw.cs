using Microsoft.CodeAnalysis;
using SourceCrafter.LiteSpeedLink.Helpers;

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

    /// <summary>Lector estatico: <c>ReadOnlySpan&lt;byte&gt;</c> -> valor o tupla, con los metodos especificos de cada tipo.</summary>
    private static void EmitReader(StringBuilder code, string name, IReadOnlyList<ITypeSymbol> types, string indent)
    {
        code.Append("\n\n").Append(indent).Append("private static ").Append(TupleOf(types)).Append(' ').Append(name).Append("(global::System.ReadOnlySpan<byte> __b)\n")
            .Append(indent).Append("{\n")
            .Append(indent).Append("    using var __s = global::MemoryPack.MemoryPackReaderOptionalStatePool.Rent(null);\n")
            .Append(indent).Append("    var __r = new global::MemoryPack.MemoryPackReader(__b, __s);\n")
            .Append(indent).Append("    return ")
            .Append(types.Count == 1 ? ReadCall(types[0]) : "(" + string.Join(", ", types.Select(ReadCall)) + ")")
            .Append(";\n")
            .Append(indent).Append('}');
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

    /// <summary>
    /// Cliente unario raw: la peticion se construye en bytes con escritores especificos y se envia con
    /// <c>GetRaw</c>/<c>GetRawAsync</c>; la respuesta se lee con lectores especificos. Streams y operaciones
    /// sin retorno se envian igual; el cuerpo de respuesta se ignora salvo out/ref. Streams y Task sin
    /// resultado siguen por la API tipada.
    /// </summary>
    private static bool TryGenerateRawClientMethod(StringBuilder code, StringBuilder helpers, INamedTypeSymbol iFace, IMethodSymbol method, ref int rawIndex)
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

        if (request.Count > 0) EmitWriter(helpers, reqName, [.. request.Select(p => p.Type)], "");
        if (readsResponse) EmitReader(helpers, resName, responseTypes, "");

        string reqArgs = request.Count > 0 ? $"{reqName}({string.Join(", ", request.Select(p => p.Name))})" : "default";

        // Async: los out/ref viajan en la tupla de retorno (misma forma que la API tipada).
        string name = method.Name.EndsWith("Async") ? method.Name : method.Name + "Async";
        string asyncParams = string.Join(", ", method.Parameters.Where(p => p.RefKind is not RefKind.Out).Select(p => p.Type.GlobalNamespaced + " " + p.Name));

        code.Append("\n\n    public async global::System.Threading.Tasks.").Append(isTask && !isValueTask ? "Task" : "ValueTask")
            .Append(readsResponse ? "<" + TupleOf(responseTypes) + ">" : null).Append(' ').Append(name).Append('(').Append(asyncParams);

        if (tokenName is null)
            code.Append(asyncParams.Length > 0 ? ", " : null).Append(cancelTokenFullTypeName).Append(" @__token = default");

        code.Append(")\n    {\n        ").Append(readsResponse ? "var __res = " : null).Append("await __connection.GetRawAsync(").Append(serviceId).Append(", ").Append(reqArgs).Append(", ").Append(tokenName ?? "@__token")
            .Append(").ConfigureAwait(false);");

        if (readsResponse) code.Append("\n\n        return ").Append(resName).Append("(__res.Span);");

        code.Append("\n    }");

        // Contrato async sin token: la sobrecarga publica anade uno opcional y no lo implementa.
        if (isTask && tokenName is null)
            code.Append("\n\n    ").Append(method.ReturnType.GlobalNamespaced).Append(' ').Append(iFace.GlobalNamespaced).Append('.').Append(method.Name)
                .Append('(').Append(asyncParams).Append(") => ").Append(name).Append('(')
                .Append(string.Join(", ", method.Parameters.Select(p => p.Name))).Append(");");

        if (!isTask)
        {
            code.Append("\n\n    public ").Append(method.GlobalMemberSignature).Append("\n    {\n        ");

            string raw = $"__connection.GetRaw({serviceId}, {reqArgs}" + (tokenName is null ? "" : $", {tokenName}") + ")";
            string call = $"{resName}({raw}.Span)";

            if (!readsResponse)
            {
                code.Append(raw).Append(";\n    }");
            }
            else if (!hasRet)
            {
                code.Append("var __res = ").Append(call).Append(";\n");

                if (response.Count == 1)
                    code.Append("\n        ").Append(response[0].Name).Append(" = __res;");
                else
                    for (int i = 0; i < response.Count; i++)
                        code.Append("\n        ").Append(response[i].Name).Append(" = __res.Item").Append(i + 1).Append(';');

                code.Append("\n    }");
            }
            else if (response.Count == 0)
            {
                code.Append("return ").Append(call).Append(";\n    }");
            }
            else
            {
                code.Append("var __res = ").Append(call).Append(";\n");

                for (int i = 0; i < response.Count; i++)
                    code.Append("\n        ").Append(response[i].Name).Append(" = __res.Item").Append(i + 2).Append(';');

                code.Append("\n\n        return __res.Item1;\n    }");
            }
        }

        return true;
    }
}
