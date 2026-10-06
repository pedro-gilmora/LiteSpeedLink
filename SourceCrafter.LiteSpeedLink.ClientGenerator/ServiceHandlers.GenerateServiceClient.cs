using Microsoft.CodeAnalysis;
using SourceCrafter.DependencyInjection.Generation;
using SourceCrafter.LiteSpeedLink.Helpers;



//using SourceCrafter.Helpers;

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;


public sealed class ServiceClientGenerator : ServiceHandlersGenerator;

public partial class ServiceHandlersGenerator
{
    const string DedicatedStreamAttr = "SourceCrafter.LiteSpeedLink.DedicatedStreamAttribute";

    private static void GenerateServiceClient(
        ServiceProviderInfo container,
        PartialContribution contribution,
        int connectionType,
        bool isLocal,
        System.Threading.CancellationToken cancellationToken)
    {
        var compilation = container.Compilation;
        var serviceClient = container.ContainerType;

        StringBuilder clientCode = new();

        string nsStr = "";

        // Local: la superficie es IAsyncDisposable en cualquier destino. Si el contenedor de DI ya
        // es liberable, se encadena su propia liberacion antes de cerrar la conexion.
        string? localDispose = !isLocal ? null : container.ContainerDisposability switch
        {
            PartialDisposability.AsyncDisposable => @"

    async global::System.Threading.Tasks.ValueTask global::System.IAsyncDisposable.DisposeAsync()
    {
        await DisposeAsync().ConfigureAwait(false);
        await __connection.DisposeAsync().ConfigureAwait(false);
    }",
            PartialDisposability.Disposable => @"

    global::System.Threading.Tasks.ValueTask global::System.IAsyncDisposable.DisposeAsync()
    {
        Dispose();
        return __connection.DisposeAsync();
    }",
            _ => @"

    global::System.Threading.Tasks.ValueTask global::System.IAsyncDisposable.DisposeAsync() => __connection.DisposeAsync();"
        };

        var connTypeName = connectionType switch
        {
            0 => "Memory",
            1 => "Udp",
            2 => "Tcp",
            UdsConnection => "Uds",
            _ => "Quic"
        };

        //[global::Jab.ServiceProvider]
        clientCode.Append(@"using SourceCrafter.LiteSpeedLink.Client;
");

        if (serviceClient.ContainingNamespace is { IsGlobalNamespace: false } nss)
        {
            clientCode.Append(@"
namespace ").Append(nsStr = nss.ToDisplayString()).Append(@";
");
        }

        string typeShortName = serviceClient.TypeNameFormat;

        clientCode.Append(PlatformAttributes(connectionType)).Append(@"
public partial class ").Append(typeShortName).Append(isLocal ? " : global::System.IAsyncDisposable" : null).Append(@"
{
    private readonly global::SourceCrafter.LiteSpeedLink.Client.").Append(connTypeName).Append(@"Connection __connection;

    public ").Append(typeShortName).Append(isLocal
            // Local: mismo constructor en cualquier destino
            ? connectionType is 0
                ? @"(string name)
    {
        __connection = name.AsMemoryConnection();"
                : @"(string name)
    {
        __connection = new global::SourceCrafter.LiteSpeedLink.Client.UdsConnection(" + LocalUdsPath + ");"
            : connectionType > 0
            // QUIC, TCP, UDP
            ? @"(string hostname, int port)
    {
        __connection = new global::System.Net.DnsEndPoint(hostname, port).As" + connTypeName + "Connection();"
            // Memory
            : @"(string rpcName)
    {
        __connection = rpcName.AsMemoryConnection();").Append(@"
    }").Append(localDispose).Append(@"

    private readonly global::System.Threading.Lock __servicesLock = new();");

        string header = nsStr.Length > 0
            ? "using SourceCrafter.LiteSpeedLink.Client;\n\nnamespace " + nsStr + ";\n"
            : "using SourceCrafter.LiteSpeedLink.Client;\n";

        string
            typeName = serviceClient.GlobalNamespaced,
            nsMeta = serviceClient.ContainingNamespace.MetadataLongName,
            typeMeta = serviceClient.MetadataLongName,
            justTypeMeta = nsMeta.Length > 0 ? typeMeta.Replace(nsMeta, "") : typeMeta,
            hintName = nsStr.Length > 0 ? nsStr + "." + justTypeMeta : justTypeMeta;

        clientCode.Append(@"
}");

        contribution.AddSource(hintName + ".client", clientCode.ToString());

        foreach (var iFace in serviceClient.GetAttributes()
            .Where(a => a.AttributeClass is { IsGenericType: true } ac
                && ac.ConstructedFrom.ToDisplayString() == ServiceUnitAttr)
            .Select(a => a.AttributeClass!.TypeArguments[0])
            .OfType<INamedTypeSymbol>()
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
        {
            var fullTypeName = iFace.GlobalNamespaced;
            var ifaceName = iFace.Name;
            var propName = ifaceName.Length > 1 && ifaceName[0] == 'I' && char.IsUpper(ifaceName[1]) ? ifaceName.Substring(1) : ifaceName;
            var implName = propName + "Client";
            var fieldName = "__" + char.ToLowerInvariant(propName[0]) + propName.Substring(1);

            clientCode = new StringBuilder(header).Append(@"
public partial class ").Append(typeShortName).Append(@"
{
    private ").Append(implName).Append("? ").Append(fieldName).Append(@";

    public ").Append(implName).Append(' ').Append(propName).Append(@"
    {
        get
        {
            if (").Append(fieldName).Append(@" is null)
                lock (__servicesLock)
                    ").Append(fieldName).Append(@" ??= new(__connection, this);

            return ").Append(fieldName).Append(@";
        }
    }

    public sealed class ").Append(implName).Append("(global::SourceCrafter.LiteSpeedLink.Client.").Append(connTypeName).Append("Connection __connection, ").Append(typeShortName).Append(" __provider) : ").Append(fullTypeName).Append(@"
    {");

            var membersStart = clientCode.Length;
            var rawHelpers = new StringBuilder();
            int rawIndex = 0;

            foreach (var member in iFace.GetMembers())
            {
                if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false } method)
                {
                    // #14: solo QUIC tiene pool que evitar; el resto ignora [DedicatedStream] (el contrato se comparte entre hosts).
                    string conn = connectionType == 3 && method.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == DedicatedStreamAttr)
                        ? "__connection.Dedicated" : "__connection";

                    if (TryGenerateProcessedClientMethod(clientCode, rawHelpers, container, iFace, method, contribution, conn, ref rawIndex)) continue;

                    if (TryGenerateStreamClientMethod(clientCode, method)) continue;

                    if (TryGenerateRawClientMethod(clientCode, rawHelpers, iFace, method, conn, ref rawIndex)) continue;

                    bool
                        hasEmptyParams = method.Parameters.IsDefaultOrEmpty,
                        isTask = method.ReturnType.TryGetAsyncType(out var returnType, out var hasReturnType, out var isValueTask),
                        needsCancelToken = true,
                        useReqTypesComma = false;

                    hasReturnType = !method.ReturnsVoid || (isTask && hasReturnType);

                    string
                        methodName = method.NameOnly,
                        globalizedMethodName = method.GlobalNamespaced,
                        returnFullTypeName = returnType.GlobalNonGenericNamespace,
                        cancelTokenParam = null!,
                        opMethod = hasReturnType
                            ? returnType.GlobalNonGenericNamespace switch
                            {
                                "global::System.Collections.Generic.IAsyncEnumerable" => "Enumerate",
                                _ => "Get"
                            }
                            : "Send";

                    Action?
                        responseTypes = hasReturnType ? () => clientCode.Append(returnFullTypeName) : null,
                        responseDeconstruct = hasReturnType ? () => clientCode.Append("@__response") : null,
                        requestTypes = null,
                        requestParams = null;

                    Action? methodParams = null;

                    int outCount = responseTypes != null ? 1 : 0, inCount = 0;

                    //string methodSignature = method.ToMinimalDisplayString(model, 0).Replace(iFace.ToMinimalDisplayString(model, 0) + ".", "");

                    bool paramsComma = false, asyncParamsComma = false;

                    if (!hasEmptyParams)
                    {
                        foreach (var param in method.Parameters)
                        {
                            var paramType = param.Type.GlobalNamespaced;

                            switch (param.RefKind)
                            {
                                case RefKind.Ref or RefKind.RefReadOnly or RefKind.RefReadOnlyParameter:

                                    methodParams += () =>
                                    {
                                        if (Exchange(ref paramsComma)) clientCode.Append(", ");

                                        clientCode.Append(param.GetString());
                                    };

                                    inCount++;

                                    outCount++;

                                    responseDeconstruct += () =>
                                        clientCode.Append(", ").Append(param.Name);

                                    responseTypes += () =>
                                        clientCode
                                            .Append(", ")
                                            .Append(paramType);

                                    requestTypes += () =>
                                        (Exchange(ref useReqTypesComma)
                                           ? clientCode.Append(", ")
                                           : clientCode)
                                        .Append(param.Type.GlobalNamespaced);

                                    requestParams += () =>
                                        (Exchange(ref asyncParamsComma)
                                           ? clientCode.Append(", ")
                                           : clientCode).Append(param.Name);

                                    continue;

                                case RefKind.Out:

                                    outCount++;

                                    responseDeconstruct += () =>
                                        clientCode
                                            .Append(", ")
                                            .Append(param.Name);

                                    responseTypes += () =>
                                        clientCode
                                            .Append(", ")
                                            .Append(paramType);

                                    continue;

                                default:

                                    methodParams += () =>
                                    {
                                        if (Exchange(ref paramsComma)) clientCode.Append(", ");

                                        clientCode.Append(param.GetString());
                                    };

                                    if (cancelTokenParam == null && paramType == cancelTokenFullTypeName)
                                    {
                                        cancelTokenParam = param.NameOnly;

                                        needsCancelToken = false;

                                        continue;
                                    }

                                    inCount++;

                                    requestTypes += () =>
                                        (Exchange(ref useReqTypesComma)
                                           ? clientCode.Append(", ")
                                           : clientCode)
                                        .Append(param.Type.GlobalNamespaced);

                                    requestParams += () =>
                                        (Exchange(ref asyncParamsComma)
                                           ? clientCode.Append(", ")
                                           : clientCode).Append(param.Name);

                                    break;
                            }
                            ;
                        }
                    }

                    var serviceId = GetServiceId(globalizedMethodName);
                    bool generatingSync = false;

                    clientCode.Append(@"

    public global::System.Threading.Tasks.").Append(!isTask || isValueTask ? "Value" : null).Append("Task");

                    if (outCount > 0)
                    {
                        clientCode.Append('<');

                        if (outCount > 1)
                        {
                            clientCode.Append('(');

                            responseTypes!.Invoke();

                            clientCode.Append(')');
                        }
                        else
                        {
                            responseTypes!.Invoke();
                        }

                        clientCode.Append('>');
                    }

                    clientCode.AddSpace().Append(methodName);

                    if (!methodName.EndsWith("Async"))
                    {
                        clientCode.Append("Async");
                    }

                    clientCode.Append('(');

                    methodParams?.Invoke();

                    if (needsCancelToken)
                    {
                        cancelTokenParam = "@__token";
                        needsCancelToken = false;

                        if (Exchange(ref paramsComma)) clientCode.Append(", ");

                        clientCode
                            .Append(cancelTokenFullTypeName)
                            .Append(" @__token = default");
                    }

            //        System.Console.WriteLine(@""Client sent call to: ").Append(globalizedMethodName).Append(@"
            //ServiceId: ").Append(serviceId).Append(@""");

                    clientCode.Append(@")
    {        
        ");

                    clientCode
                        .Append("return ").Append(conn).Append('.')
                        .Append(opMethod);
                    
                    if(!generatingSync)
                        clientCode.Append("Async");

                    bool useComma = false, closeTag = false;

                generateCall:
                    if (inCount > 0)
                    {
                        useComma = true;
                        closeTag = true;
                        if (inCount > 1)
                        {
                            clientCode
                                .Append("<(");

                            requestTypes!.Invoke();

                            clientCode
                                .Append(')');
                        }
                        else
                        {
                            clientCode
                                .Append('<');

                            requestTypes!.Invoke();
                        }
                    }

                    if (responseTypes != null)
                    {
                        closeTag |= true;

                        if (useComma)
                        {
                            clientCode.Append(", ");
                        }

                        if (outCount > 0)
                        {
                            if (outCount > 1)
                            {
                                clientCode.Append('(');

                                responseTypes!.Invoke();

                                clientCode.Append(')');
                            }
                            else
                            {
                                responseTypes!.Invoke();
                            }
                        }
                    }

                    if (closeTag)
                    {
                        clientCode
                            .Append('>');
                    }

                    clientCode
                        .Append('(')
                        .Append(serviceId)
                        .Append(", ");

                    if (inCount > 0)
                    {
                        if (inCount == 1)
                        {
                            requestParams?.Invoke();
                        }
                        else
                        {
                            clientCode.Append('(');

                            requestParams?.Invoke();

                            clientCode.Append(')');
                        }
                    }

                   if(!generatingSync) clientCode.Append(", ").Append(cancelTokenParam);
                    
                    clientCode.Append(@");");

                    if (generatingSync && hasReturnType && outCount > 1)
                    {
                        clientCode.Append(@"

        return @__response;
    }");
                    }
                    else
                    {
                        clientCode.Append(@"
    }");
                    }

                    if (!isTask && !generatingSync)
                    {
                        paramsComma = asyncParamsComma = useReqTypesComma = false;

                        clientCode.Append(@"

    public ");

                        clientCode
                            .Append(method.GlobalMemberSignature)
                            .Append(@"
    {
        ");

                        if (outCount > 1)
                        {
                            if (hasReturnType)
                            {
                                clientCode.Append(returnFullTypeName).Append(@" @__response;

        (");

                                responseDeconstruct!.Invoke();

                                clientCode.Append(@") = ");
                            }
                            else
                            {
                                clientCode.Append('(');

                                responseDeconstruct!.Invoke();

                                clientCode.Append(@") = ");
                            }
                        }
                        else if (outCount == 1)
                        {
                            if (hasReturnType)
                            {
                                clientCode.Append("return ");
                            }
                            else
                            {
                                responseDeconstruct!.Invoke();

                                clientCode.Append(" = ");
                            }
                        }

                        clientCode.Append(conn).Append('.').Append(opMethod);

                        generatingSync = true;
                        useComma = closeTag = paramsComma = useReqTypesComma = asyncParamsComma = false;
                        goto generateCall;
                    }
                }
            }

            clientCode.Append(rawHelpers.Replace("\n", "\n    "));

            // Los miembros se emiten a 4 espacios; se anidan un nivel dentro de la clase cliente.
            clientCode.Replace("\n", "\n    ", membersStart, clientCode.Length - membersStart);
            clientCode.Replace("\n    \n", "\n\n", membersStart, clientCode.Length - membersStart).Replace("\n    \r\n", "\n\r\n", membersStart, clientCode.Length - membersStart);

            clientCode.Append(@"
    }
}");

            contribution.AddSource(hintName + "." + propName + ".client", clientCode.ToString());
        }
    }
    /// <summary>
    /// Stream (IEnumerable/IAsyncEnumerable): el contrato se implementa con Enumerate/EnumerateAsync de la
    /// conexion; los sync ganan una sobrecarga *Async con token. Peticion = parametro o tupla (misma forma que el host).
    /// </summary>
    private static bool TryGenerateStreamClientMethod(StringBuilder code, IMethodSymbol method)
    {
        if (method.ReturnType is not INamedTypeSymbol { IsGenericType: true } rt
            || rt.ConstructedFrom.ToDisplayString() is not ("System.Collections.Generic.IAsyncEnumerable<T>" or "System.Collections.Generic.IEnumerable<T>")
            || method.Parameters.Any(p => p.RefKind is not RefKind.None))
            return false;

        bool isAsync = rt.Name == "IAsyncEnumerable";
        var token = method.Parameters.FirstOrDefault(p => p.Type.GlobalNamespaced == cancelTokenFullTypeName);
        var request = method.Parameters.Where(p => !SymbolEqualityComparer.Default.Equals(p, token)).ToList();
        string item = rt.TypeArguments[0].GlobalNamespaced, id = GetServiceId(method.GlobalNamespaced).ToString();
        string generics = request.Count == 0 ? item : TupleOf([.. request.Select(p => p.Type)]) + ", " + item;
        string payload = request.Count switch { 0 => "", 1 => ", " + request[0].Name, _ => ", (" + string.Join(", ", request.Select(p => p.Name)) + ")" };

        string Call(string op, string tokenArg) => $"__connection.{op}<{generics}>({id}{payload}{tokenArg})!;";

        code.Append("\n\n    public ").Append(method.GlobalMemberSignature).Append(" => ")
            .Append(Call(isAsync ? "EnumerateAsync" : "Enumerate", token is null ? "" : ", " + token.Name));

        if (!isAsync && token is null)
            code.Append("\n\n    public global::System.Collections.Generic.IAsyncEnumerable<").Append(item).Append("> ").Append(method.Name).Append("Async(")
                .Append(string.Concat(request.Select(p => p.Type.GlobalNamespaced + " " + p.Name + ", "))).Append(cancelTokenFullTypeName).Append(" @__token = default) => ")
                .Append(Call("EnumerateAsync", ", @__token"));

        return true;
    }
}

