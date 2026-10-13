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

        var clientCode = new CodeBuilder().Append("#nullable enable annotations\n");

        string nsStr = "";

        // La conexion la libera el Dispose/DisposeAsync raiz que emite el generador de DI.
        // Local es IAsyncDisposable en cualquier destino (Memory en Windows, UDS fuera).
        contribution.AddDisposer("__connection", connectionType is 0 or 1 && !isLocal
            ? PartialDisposability.Disposable
            : PartialDisposability.AsyncDisposable);

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

        // Con [JsonSetting<RemoteOptions|LocalOptions>] los parametros del constructor son opcionales:
        // el explicito gana, el omitido sale del setting generado por MsConfiguration.
        var settings = EndpointSettings(container, connectionType is 1 or 2 or 3);
        string Or(string arg, string member) => settings is null ? arg : arg + " ?? " + settings + "." + member;
        string Param(string type, string name) => settings is null ? type + " " + name : type + (type is "int" ? "?" : null) + " " + name + " = null";

        clientCode.Append(PlatformAttributes(connectionType)).Append(@"
public partial class ").Append(typeShortName).Append(@"
{
    private readonly global::SourceCrafter.LiteSpeedLink.Client.").Append(connTypeName).Append(@"Connection __connection;

    public ").Append(typeShortName).Append(connectionType > 0 && !isLocal
            // QUIC, TCP, UDP
            ? @"(" + Param("string", "hostname") + ", " + Param("int", "port") + @")
    {
        __connection = new global::System.Net.DnsEndPoint(" + Or("hostname", "Host") + ", " + Or("port", "Port") + ").As" + connTypeName + "Connection();"
            : "(" + Param("string", isLocal ? "name" : "rpcName") + @")
    {" + (settings is null ? null : @"
        " + (isLocal ? "name" : "rpcName") + " ??= " + settings + ".Name;") + (connectionType is 0
                ? @"
        __connection = " + (isLocal ? "name" : "rpcName") + ".AsMemoryConnection(" + (settings is null ? null : "(int)" + settings + ".Timeout.TotalMilliseconds") + ");"
                : @"
        __connection = new global::SourceCrafter.LiteSpeedLink.Client.UdsConnection(" + LocalUdsPath + ");")).Append(@"
    }").Append(@"

    private readonly global::System.Threading.Lock __servicesLock = new();");

        string header = nsStr.Length > 0
            ? "#nullable enable annotations\nusing SourceCrafter.LiteSpeedLink.Client;\n\nnamespace " + nsStr + ";\n"
            : "#nullable enable annotations\nusing SourceCrafter.LiteSpeedLink.Client;\n";

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

            clientCode = new CodeBuilder().Append(header).Append(@"
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

            // Los miembros se emiten a 4 espacios; la profundidad los anida un nivel dentro de la clase cliente.
            clientCode.Indent();
            var membersStart = clientCode.Length;
            var rawHelpers = new CodeBuilder();
            int rawIndex = 0;

            foreach (var member in iFace.GetMembers())
            {
                if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false } method)
                {
                    // #14: solo QUIC tiene pool que evitar; el resto ignora [DedicatedStream] (el contrato se comparte entre hosts).
                    string conn = connectionType == 3 && method.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == DedicatedStreamAttr)
                        ? "__connection.Dedicated" : "__connection";

                    ReportRetryDiagnostics(method, contribution);
                    ReportCacheKeyIgnored(method, contribution);

                    if (TryGenerateProcessedClientMethod(clientCode, rawHelpers, container, iFace, method, contribution, conn, ref rawIndex)) continue;

                    if (TryGenerateStreamClientMethod(clientCode, method, connectionType, conn)) continue;

                    if (TryGenerateRawClientMethod(clientCode, rawHelpers, iFace, method, conn, ref rawIndex, container, contribution)) continue;

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
                        clientCode.Append('<').Parens(responseTypes, outCount > 1).Append('>');
                    }

                    clientCode.Append(' ').Append(methodName);

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
                        clientCode.Append('<').Parens(requestTypes, inCount > 1);
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
                            clientCode.Parens(responseTypes, outCount > 1);
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
                        clientCode.Parens(requestParams, inCount > 1);
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

            // Los helpers se emiten a columna 0: dos niveles dentro (clase raiz + clase cliente).
            clientCode.Indent().Append(rawHelpers).Unindent().Unindent();

            clientCode.Append(@"
    }
}");

            // Solo los pipelines de cliente usan __provider; sin ellos el parametro sobra (CS9113).
            if (clientCode.ToString(membersStart, clientCode.Length - membersStart).IndexOf("__provider", StringComparison.Ordinal) < 0)
                clientCode.Replace(", " + typeShortName + " __provider)", ")").Replace("new(__connection, this)", "new(__connection)");

            contribution.AddSource(hintName + "." + propName + ".client", clientCode.ToString());
        }
    }
    /// <summary>
    /// Stream (IEnumerable/IAsyncEnumerable): el contrato se implementa con Enumerate/EnumerateAsync de la
    /// conexion; los sync ganan una sobrecarga *Async con token. Peticion = parametro o tupla (misma forma que el host).
    /// </summary>
    private static bool TryGenerateStreamClientMethod(CodeBuilder code, IMethodSymbol method, int connectionType, string conn)
    {
        if (!IsStream(method.ReturnType) || method.Parameters.Any(p => p.RefKind is not RefKind.None))
            return false;

        var rt = (INamedTypeSymbol)method.ReturnType;
        bool isAsync = rt.Name == "IAsyncEnumerable";
        var token = method.Parameters.FirstOrDefault(p => p.Type.GlobalNamespaced == cancelTokenFullTypeName);
        var request = method.Parameters.Where(p => !SymbolEqualityComparer.Default.Equals(p, token)).ToList();
        string item = rt.TypeArguments[0].GlobalNamespaced, id = GetServiceId(method.GlobalNamespaced).ToString();
        string generics = request.Count == 0 ? item : TupleOf([.. request.Select(p => p.Type)]) + ", " + item;
        string payload = request.Count switch { 0 => "", 1 => ", " + request[0].Name, _ => ", (" + string.Join(", ", request.Select(p => p.Name)) + ")" };

        // [Stream(IdleTimeoutMs)]: watchdog fijado por el contrato (solo memoria lo aplica en cliente).
        int idle = 0;
        if (connectionType == 0 && method.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "SourceCrafter.LiteSpeedLink.StreamAttribute") is { } streamAttr)
            foreach (var arg in streamAttr.NamedArguments)
                if (arg.Key == "IdleTimeoutMs") idle = (int)arg.Value.Value!;
        string idleArg = idle == 0 ? "" : ", " + idle;

        string Call(string op, string tokenArg) => $"{conn}.{op}<{generics}>({id}{payload}{(op == "EnumerateAsync" ? idleArg : "")}{tokenArg})!;";

        code.Append("\n\n    public ").Append(method.GlobalMemberSignature).Append(" => ")
            .Append(Call(isAsync ? "EnumerateAsync" : "Enumerate", token is null ? "" : ", " + token.Name));

        if (!isAsync && token is null)
            code.Append("\n\n    public global::System.Collections.Generic.IAsyncEnumerable<").Append(item).Append("> ").Append(method.Name).Append("Async(")
                .Append(string.Concat(request.Select(p => p.Type.GlobalNamespaced + " " + p.Name + ", "))).Append(cancelTokenFullTypeName).Append(" @__token = default) => ")
                .Append(Call("EnumerateAsync", ", @__token"));

        return true;
    }
}

