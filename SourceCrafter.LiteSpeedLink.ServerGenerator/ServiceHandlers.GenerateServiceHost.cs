using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SourceCrafter.DependencyInjection.Generation;
using SourceCrafter.LiteSpeedLink.Helpers;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

public sealed class ServiceHostGenerator : ServiceHandlersGenerator;

public partial class ServiceHandlersGenerator
{
    private const string IServiceUnit = "global::SourceCrafter.LiteSpeedLink.IServiceUnit";

    private static void GenerateServiceHost(
        ServiceProviderInfo container,
        PartialContribution contribution,
        int connectionType,
        bool isLocal,
        CancellationToken cancelToken)
    {
        var compilation = container.Compilation;
        var serviceHost = container.ContainerType;
        var model = container.SemanticModel;

        bool
            isMsLoggerInstalled = compilation.GetTypeByMetadataName("Microsoft.Extensions.Logging.ILogger") != null,
            isMsConsoleLoggerInstalled = compilation.GetTypeByMetadataName("Microsoft.Extensions.Logging.ConsoleLoggerExtensions") != null;

        // La disposability efectiva del contenedor la calcula el generador de DI: aqui ya no
        // se deduce servicio a servicio.
        var containerDisposability = container.ContainerDisposability;

        var hostCode = new CodeBuilder().Append("#nullable enable annotations");

        if (isMsLoggerInstalled)
            hostCode.Line("using global::Microsoft.Extensions.Logging;");

        string nsStr = "";

        string? typeName = serviceHost.TypeNameFormat;
        string? certParam = connectionType is 2 or 3
            ? ",\nglobal::System.Security.Cryptography.X509Certificates.X509Certificate2" + (connectionType == 2 ? "?" : null) + " certificate"
            : null;

        var (context, startMethod, returnType, disposableInterface, asyncKeyword, awaitKeyword, asyncSuffix) = connectionType switch
        {
            UdsConnection => ("global::SourceCrafter.LiteSpeedLink.RequestContext",
                  "StartUdsServer",
                  "global::System.Net.Sockets.Socket",
                  "global::System.IDisposable",
                  null,
                  null,
                  null),
            3 => ("global::SourceCrafter.LiteSpeedLink.RequestContext",
                  "StartQuicServerAsync",
                  "global::System.Threading.Tasks.ValueTask<global::System.Net.Quic.QuicListener>",
                  "global::System.IAsyncDisposable",
                  "async ",
                  "await ",
                  "Async"),
            2 => ("global::SourceCrafter.LiteSpeedLink.RequestContext",
                  "StartTcpServer",
                  "global::System.Net.Sockets.TcpListener",
                  "global::System.Disposable",
                  null,
                  null,
                  null),
            1 => ("global::SourceCrafter.LiteSpeedLink.UdpRequestContext",
                  "StartUdpServer",
                  "global::System.Net.Sockets.UdpClient",
                  "global::System.IDisposable",
                  null,
                  null,
                  null),
            _ => ("global::SourceCrafter.LiteSpeedLink.MemoryRequestContext",
                  "StartMemoryServerAsync",
                  "global::System.IDisposable",
                  "global::System.IDisposable",
                  null,
                  null,
                  null),
        };

        var handlerReturnType = "global::SourceCrafter.LiteSpeedLink.ResponseStatus";

        var handlerType = context.Replace("Context", "Handler");

        if (serviceHost.ContainingNamespace is { IsGlobalNamespace: false } ns)
            hostCode.Blank().Line("namespace ").Append(nsStr = ns.ToDisplayString()).Append(';');

        hostCode.Blank().Append(PlatformAttributes(connectionType).TrimStart()).Line("public partial class ").Append(typeName).Open();

        if (isMsLoggerInstalled)
        {
            hostCode.Line("static readonly global::Microsoft.Extensions.Logging.ILoggerFactory ___loggerFactory = global::Microsoft.Extensions.Logging.LoggerFactory.Create(static builder =>")
                .Open()
                .Line("builder")
                .Indent()
                .Line(".AddFilter(\"Microsoft\", global::Microsoft.Extensions.Logging.LogLevel.Warning)")
                .Line(".AddFilter(\"System\", global::Microsoft.Extensions.Logging.LogLevel.Warning)")
                .Line(".AddFilter(\"").Append(serviceHost.GlobalNamespaced.Replace("global::", "")).Append("\", global::Microsoft.Extensions.Logging.LogLevel.Debug)");

            if (isMsConsoleLoggerInstalled) hostCode.Line(".AddConsole()");

            hostCode.Append(';').Unindent().Close(");");
        }

        // Local: misma firma (nombre) y misma superficie (IAsyncDisposable) en cualquier destino.
        if (isLocal) returnType = "global::System.IAsyncDisposable";

        var (endpointParam, endpointArg) = isLocal ? ("string name", "name") : connectionType is 0 ? ("string memoryRpcName", "memoryRpcName") : ("int port", "port");

        hostCode.Blank().Line("public static ").Append(returnType).Append(" Start").Append(asyncSuffix).Append('(')
            .Indent().Line(endpointParam).Append(certParam).Append(',')
            .Line("global::System.Threading.CancellationToken cancelToken = default) => __Start").Append(asyncSuffix).Append("(new ").Append(typeName).Append("(), ").Append(endpointArg).Append(certParam is null ? null : ", certificate").Append(", cancelToken);")
            .Unindent();

        // Endpoint omitido: el de [JsonSetting<RemoteOptions|LocalOptions>] del contenedor (MsConfiguration).
        if (EndpointSettings(container, connectionType is 1 or 2 or 3) is { } settings)
            hostCode.Blank().Line("public static ").Append(returnType).Append(" Start").Append(asyncSuffix).Append('(').Append(certParam is null ? null : connectionType is 2
                    ? "global::System.Security.Cryptography.X509Certificates.X509Certificate2? certificate = default, "
                    : "global::System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, ").Append("global::System.Threading.CancellationToken cancelToken = default)")
                .Open()
                .Line("var provider = new ").Append(typeName).Append("();")
                .Line("return __Start").Append(asyncSuffix).Append("(provider, provider.").Append(settings).Append(connectionType is 1 or 2 or 3 ? ".Port" : ".Name").Append(certParam is null ? null : ", certificate").Append(", cancelToken);")
                .Close();

        hostCode.Blank().Line("private static ").Append(asyncKeyword).Append(returnType).Append(" __Start").Append(asyncSuffix).Append('(')
            .Indent().Line(typeName).Append(" provider,")
            .Line(endpointParam).Append(certParam).Append(',')
            .Line("global::System.Threading.CancellationToken cancelToken)").Unindent()
            .Open()
            .Line("return ").Append(awaitKeyword).Append(isLocal ? "new global::SourceCrafter.LiteSpeedLink.LocalHost(" : null).Append("global::SourceCrafter.LiteSpeedLink.Server.").Append(startMethod).Append('(')
            .Indent().Line(isLocal ? (connectionType is 0 ? "name" : LocalUdsPath) : endpointArg).Append(',')
            .Line(connectionType > 1 ? "new __Handler(provider)" : "provider.HandleRequestsAsync").Append(',')
            .Line(containerDisposability switch
            {
                PartialDisposability.AsyncDisposable => "() => provider.DisposeAsync().GetAwaiter().GetResult()",
                PartialDisposability.Disposable => "() => provider.Dispose()",
                _ => "() => {}"
            }).Append(',');

        if (certParam != null) hostCode.Line("certificate,");

        hostCode.Line("cancelToken)").Append(isLocal ? ")" : null).Append(awaitKeyword != null ? ".ConfigureAwait(false)" : null).Append(';')
            .Unindent().Close();

        if (connectionType > 1)
            hostCode.Blank().Line("private readonly struct __Handler(").Append(typeName).Append(" provider) : global::SourceCrafter.LiteSpeedLink.IRequestHandler")
                .Open()
                .Line("public global::System.Threading.Tasks.ValueTask<global::SourceCrafter.LiteSpeedLink.ResponseStatus> HandleAsync(long id, global::SourceCrafter.LiteSpeedLink.RequestContext ctx, global::System.Threading.CancellationToken token)")
                .Line("=> provider.HandleRequestsAsync(id, ctx, token);", 1)
                .Close();

        // Un metodo por operacion: async (pooled) solo si algo de su camino espera (ver EndOperation).
        var pooled = PooledBuilder(true);
        List<(long Id, string Signature, string Name, bool Sync)> operations = [];

        int providerTypeId = SymbolEqualityComparer.Default.GetHashCode(serviceHost);

        string
            fullContainerTypeName = serviceHost.GlobalNamespaced,
            nsMeta = serviceHost.ContainingNamespace.MetadataLongName,
            typeMeta = serviceHost.MetadataLongName,
            justTypeMeta = nsMeta.Length > 0 ? typeMeta.Replace(nsMeta, "") : typeMeta,
            hintName = nsStr.Length > 0 ? nsStr + "." + justTypeMeta : justTypeMeta;

        var services = IndexServices(container);

        CodeBuilder policyTypes = new();
        HashSet<string> emittedPolicies = [];

        foreach (var dependency in container.Services)
        {
            //     var attrCls = attr.AttributeClass!;

            //     var attrSyntax = attr.ApplicationSyntaxReference!.GetSyntax();

            //     if (!IsValidServiceAttribute(
            //             compilation.GetSemanticModel(attrSyntax.SyntaxTree),
            //             attr,
            //             null,
            //             out var finalType,
            //             out var implType,
            //             out var iFaceType,
            //             out var lifetime,
            //             out var key,
            //             out var isValid)
            //         || !isValid
            //         || finalType.GlobalNamespaced is not { } fullDependencyTypeName
            //         || Dependencies.GetDependency(compilationId, providerTypeId, $"[{lifetime}|{finalType.GlobalNamespaced}|{key}]", cancelToken) is not { } dependency) {

            //         if(attr.AttributeClass is { Name: "SingletonAttribute" or "ScopedAttribute" or "TransientAttribute" })
            //             hostCode.Append(@"
            // #error Couldn't find registered service for ").Append(attrSyntax).Append(@"

            // "); 
            //         continue; }

            var dependencyIsAsync = dependency.AsyncKind is not PartialAsyncKind.None;

            // El tipo expuesto ya viene resuelto en la propia fila: no hace falta el mapa
            // lateral que antes evitaba retener ISymbol.
            if (dependency.ExportType is not { } exportType) continue;

            // Solo los contratos (IServiceUnit) son operaciones remotas; pipelines y demas dependencias se consumen desde los handlers.
            if (!exportType.AllInterfaces.Any(i => i.GlobalNamespaced == IServiceUnit)) continue;

            foreach (var member in exportType.GetMembers())
            {
                if (!(member is IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false } method))
                {
                    continue;
                }

                string
                    methodName = method.NameOnly,
                    globalizedMethodName = method.GlobalNamespaced;

                var serviceId = GetServiceId(globalizedMethodName);



                //System.Console.WriteLine(@""Server received call to: ").Append(globalizedMethodName).Append(@"
    
                //    ServiceId: ").Append(serviceId).Append(@""");

                var opSuffix = serviceId < 0 ? "M" + (-serviceId) : serviceId.ToString();
                var opName = "__" + methodName + "_" + opSuffix;

                hostCode.Blank().Line(pooled);

                int signatureStart = hostCode.Length - pooled.Length;

                hostCode.Line("private async global::System.Threading.Tasks.ValueTask<").Append(handlerReturnType).Append('>');

                int signatureEnd = hostCode.Length;

                hostCode.Append(' ').Append(opName).Append('(').Append(context).Append(" __context, global::System.Threading.CancellationToken __token)").Open();

                var provider = "";
                // Con middlewares la llamada va en una lambda static: los miembros del host se leen de __host (capturado en el estado).
                var self = "";

                if (dependency.Lifetime is PartialLifetime.Scoped)
                {
                    if (containerDisposability is PartialDisposability.AsyncDisposable)
                    {
                        hostCode.Append("await using ");
                    }
                    else if (containerDisposability is PartialDisposability.Disposable)
                    {
                        hostCode.Append("using ");
                    }

                    if (dependency.IsCached)
                    {
                        hostCode.Append("var ___scope = CreateScope();").Blank();
                        provider = "___scope.";
                    }
                }

                bool isAwaitable = method.ReturnType.TryGetAsyncType(out var awaitedType, out var awaitedHasRet, out _);

                bool
                    hasEmptyParams = method.Parameters.IsDefaultOrEmpty,
                    returnsType = isAwaitable ? awaitedHasRet : !method.ReturnsVoid,
                    useMetadata = returnsType || !hasEmptyParams;

                Action?
                    invokeParams = null,
                    resultExpression = null,
                    requestTypes = null,
                    requestDeconstruct = null;

                int requestParamsCount = 0, outCount = 0;
                List<ITypeSymbol> requestWireTypes = [];
                List<(IParameterSymbol Param, ITypeSymbol Type)> keyRoots = [];

                bool separateParams = false,
                    separateRequestParams = false,
                    separateResponseParams = false,
                    separateRequestTypes = false,
                    cancelTokenIsSet = false;

                var methodLocation = method.Locations.FirstOrDefault();
                var serverPost = GetStages(method.GetReturnTypeAttributes(), true, container, contribution, methodLocation);
                Action? applyPreStages = null;

                // Procesar un stream seria desempaquetar y reempaquetar cada elemento: no se aplica (igual que en el cliente).
                if (serverPost.Count > 0 && IsStream(method.ReturnType))
                {
                    contribution.ReportDiagnostic(Diagnostic.Create(PipelineUnsupported, methodLocation,
                        methodName + ": return processors aren't applied to streamed results (per-item unpack/repack)"));
                    serverPost.Clear();
                }

                if (returnsType)
                {
                    ValidateChain(compilation, awaitedType, serverPost, null, methodName + " server response", contribution, methodLocation);

                    outCount++;
                    resultExpression += () =>
                    {
                        hostCode.Append(serverPost.Count > 0 ? StageVar("___result", serverPost.Count - 1) : "___result");

                        separateResponseParams = true;
                    };
                }

                if (!hasEmptyParams)
                {
                    foreach (var param in method.Parameters)
                    {
                        var paramType = param.Type.GlobalNamespaced;

                        foreach (var paramAttr in param.GetAttributes())
                        {
                            // El lifetime y la clave se leen del atributo; el resto -nombre del
                            // miembro, forma, asincronia- lo pone la tabla que ya resolvio el
                            // generador de DI, que es la unica autoridad sobre esos nombres.
                            if (!TryResolveAnnotatedParameter(services, paramAttr, param, out var paramDependency))
                                continue; //check next attribute

                            invokeParams += () =>
                                {
                                    if (Exchange(ref separateParams)) hostCode.Append(", ");

                                    AppendResolution(hostCode, paramDependency, self);
                                };

                            goto NEXT_PARAM;
                        }

                        switch (param.Type.GlobalNonGenericNamespace)
                        {
                            case "global::Microsoft.Extensions.Logging.ILogger"
                                when isMsLoggerInstalled && param.Type is INamedTypeSymbol { IsGenericType: true, TypeArguments: [{ } genericLogger] }:

                                invokeParams += () =>
                                {
                                    (Exchange(ref separateParams) ? hostCode.Append(", ") : hostCode)
                                        .Append("___loggerFactory.CreateLogger<").Append(genericLogger.GlobalNamespaced).Append(">()");
                                };

                                continue;

                            case cancelTokenFullTypeName
                                when !cancelTokenIsSet:

                                cancelTokenIsSet = true;

                                invokeParams += () =>
                                    (Exchange(ref separateParams) ? hostCode.Append(", ") : hostCode)
                                        .Append("__token");

                                continue;
                        }

                        requestParamsCount++;

                        var serverPre = GetStages(param.GetAttributes(), true, container, contribution, methodLocation);

                        if (!StagesFit(param, serverPre))
                        {
                            contribution.ReportDiagnostic(Diagnostic.Create(PipelineUnsupported, methodLocation, RefStagesMessage(methodName, param)));
                            serverPre.Clear();
                        }

                        if (param.RefKind != RefKind.Out)
                        {
                            var wireName = serverPre.Count > 0 ? "__w_" + param.Name : param.Name;
                            var wireType = serverPre.Count > 0 ? serverPre[0].In.GlobalNamespaced : paramType;
                            requestWireTypes.Add(serverPre.Count > 0 ? serverPre[0].In : param.Type);
                            keyRoots.Add((param, param.Type));

                            if (serverPre.Count > 0)
                            {
                                ValidateChain(compilation, null, serverPre, param.Type, methodName + "(" + param.Name + ") server request", contribution, methodLocation);

                                applyPreStages += () => ApplyStages(hostCode, wireName, serverPre, true, provider, HostReject(connectionType > 0), param.Name);
                            }

                            //Register params to deconstruct request from deserialization
                            requestDeconstruct += () =>
                                (Exchange(ref separateRequestParams) ? hostCode.Append(", ") : hostCode).Append(wireName);

                            //Register types for request deserialization
                            requestTypes += () =>
                                (Exchange(ref separateRequestTypes) ? hostCode.Append(", ") : hostCode).Append(wireType);
                        }


                        //Register params usage and in/out/ref tuple types
                        switch (param.RefKind)
                        {
                            case RefKind.In:

                                invokeParams += () =>
                                    (Exchange(ref separateParams) ? hostCode.Append(", ") : hostCode)
                                        .Append("in ").Append(param.Name);

                                break;

                            case RefKind.Ref or RefKind.RefReadOnly or RefKind.RefReadOnlyParameter:

                                outCount++;

                                invokeParams += () =>
                                    (Exchange(ref separateParams) ? hostCode.Append(", ") : hostCode)
                                        .Append("ref ").Append(param.Name);

                                resultExpression += () =>
                                {
                                    (Exchange(ref separateResponseParams)
                                        ? hostCode.Append(", ")
                                        : hostCode)
                                    .Append(param.Name);
                                };

                                break;
                            case RefKind.Out:

                                outCount++;

                                requestParamsCount--;

                                invokeParams += () =>
                                    (Exchange(ref separateParams) ? hostCode.Append(", ") : hostCode)
                                        .Append("out var ").Append(param.Name);

                                resultExpression += () =>
                                {
                                    (Exchange(ref separateResponseParams)
                                        ? hostCode.Append(", ")
                                        : hostCode)
                                    .Append(param.Name);
                                };

                                break;
                            default:

                                invokeParams += () =>
                                    (Exchange(ref separateParams) ? hostCode.Append(", ") : hostCode)
                                        .Append(param.Name);

                                break;
                        }
                        ;

                    NEXT_PARAM:;
                    }
                }

                var serverCache = GetCache(method, true, contribution);
                // Sin [CacheKey] la clave son los bytes de la peticion; con el, la clave tipada tras leer los argumentos.
                var serverKey = serverCache is null ? null : GetCacheKey(method, keyRoots, compilation, false, contribution);
                if (serverKey is null) serverCache = null;
                bool typedKey = serverKey is { Explicit: true };

                if (serverCache is { } sc)
                {
                    var manager = typedKey
                        ? "global::SourceCrafter.LiteSpeedLink.CacheManager<" + serverKey!.Type + ", global::System.ReadOnlyMemory<byte>>"
                        : "global::SourceCrafter.LiteSpeedLink.ServerCacheManager";

                    // Por metodo, creado al primer uso (thread-safe): sin coste de memoria para operaciones nunca llamadas.
                    policyTypes.PrependLine(2, "private ").Append(manager).Append(" __cache").Append(opSuffix).Append(';')
                        .Blank().Line("private ").Append(manager).Append(" __Cache").Append(opSuffix).Append(" =>")
                        .Line("global::System.Threading.LazyInitializer.EnsureInitialized(ref __cache", 1).Append(opSuffix)
                        .Append(", static () => new ").Append(manager).Append('(').Append(sc.DurationMs).Append(", ").Append(sc.Capacity).Append("));");

                    EmitWriter(policyTypes, "__Res" + opSuffix,
                        [serverPost.Count > 0 ? serverPost[serverPost.Count - 1].Out : isAwaitable ? awaitedType! : method.ReturnType]);
                }

                // Con param processors (p.ej. autorizacion) la consulta va despues: un acierto no puede saltarselos.
                if (serverCache != null && applyPreStages == null && !typedKey) OpenServerCache(hostCode, opSuffix, "__context.Body");

                if (requestParamsCount > 0 && requestTypes != null && requestDeconstruct != null)
                {
                    var readerName = "__Req" + opSuffix;

                    EmitReader(policyTypes, readerName, requestWireTypes);

                    hostCode.Append("var ").Parens(requestDeconstruct, requestParamsCount > 1)
                        .Append(" = ").Append(readerName).Append("(__context.Body.Span);").Blank();
                }

                applyPreStages?.Invoke();

                if (serverCache != null && (applyPreStages != null || typedKey)) OpenServerCache(hostCode, opSuffix, typedKey ? serverKey!.Expr : "__context.Body");

                var serverRetry = GetRetry(method, true);
                ReportRetryDiagnostics(method, contribution);
                ReportCacheKeyIgnored(method, contribution);
                var mws = GetMiddlewares(method, true, container, contribution);
                // Sync si nada de la llamada espera ni pausa y todos tienen IMiddleware: no contagia async al handler de Memory.
                bool mwAsync = isAwaitable || dependencyIsAsync || serverRetry is { IntervalMs: > 0 } || mws.Any(m => !m.Sync || m.Service.AsyncKind is not PartialAsyncKind.None);
                List<string>? chain = mws.Count > 0 ? MiddlewareChain(mws, mwAsync, provider, method, contribution) : null;
                if (chain is { Count: > 0 }) self = "__host.";
                int callStart = hostCode.Length;

                if (isAwaitable) hostCode.Append("await ");

                if (dependencyIsAsync) hostCode.Append("(await ");

                AppendResolution(hostCode, dependency, provider.Length > 0 ? provider : self);

                if (dependencyIsAsync) hostCode.Append(".ConfigureAwait(false))");

                hostCode.Append('.').Append(methodName).Parens(invokeParams).Append(isAwaitable ? ".ConfigureAwait(false)" : null);

                // Se escribe en hostCode solo para capturar el texto: la sangria de inicio de linea no es parte de la llamada.
                var call = hostCode.ToString(callStart, hostCode.Length - callStart).TrimStart();
                hostCode.Length = callStart;

                if (chain is { Count: > 0 })
                {
                    List<(string, string)> captures = call.Contains("__host.") ? [("this", "__host")] : [];
                    if (provider.Length > 0) captures.Add((provider.TrimEnd('.'), provider.TrimEnd('.')));

                    var refs = method.Parameters.Where(p => p.RefKind is not RefKind.Out && p.Type.GlobalNamespaced != cancelTokenFullTypeName
                        && !p.GetAttributes().Any(a => TryResolveAnnotatedParameter(services, a, p, out _))
                        && p.Type.GlobalNonGenericNamespace != "global::Microsoft.Extensions.Logging.ILogger");
                    captures.AddRange(refs.Select(p => (p.Name, p.Name)));

                    EmitCall(hostCode, serverRetry, call, returnsType ? "___result" : null, "__token", mwAsync,
                        (isAwaitable ? awaitedType! : method.ReturnType).GlobalNamespaced, chain, captures,
                        [.. method.Parameters.Where(p => p.RefKind is RefKind.Out or RefKind.Ref).Select(p => (p.Name, p.Type.GlobalNamespaced, p.RefKind is RefKind.Out))]);
                }
                else
                {
                if (serverRetry != null)
                {
                    // Los out se declaran fuera del bucle: un 'out var' dentro del try no seria visible al responder.
                    foreach (var p in method.Parameters.Where(p => p.RefKind == RefKind.Out))
                        hostCode.Append(p.Type.GlobalNamespaced).Append(' ').Append(p.Name).Append(';').Line();

                    call = call.Replace("out var ", "out ");
                }

                // Solo la invocacion al handler se reintenta: los server processors ya corrieron una vez.
                EmitCall(hostCode, serverRetry, call, returnsType ? "___result" : null, "__token", true,
                    (isAwaitable ? awaitedType! : method.ReturnType).GlobalNamespaced);
                }

                hostCode.Blank();

                if (returnsType && serverPost.Count > 0)
                    ApplyStages(hostCode, "___result", serverPost, true, provider, HostReject(connectionType > 0));

                if (serverCache != null)
                {
                    CloseServerCache(hostCode, opSuffix, serverPost.Count > 0 ? StageVar("___result", serverPost.Count - 1) : "___result", connectionType > 0);
                    operations.Add((serviceId, globalizedMethodName, opName, EndOperation(hostCode, signatureStart, signatureEnd, handlerReturnType, connectionType == 0)));
                    continue;
                }

                bool isStream = connectionType != 1 && returnsType && IsStream(method.ReturnType),
                    // Memory responde escribiendo en memoria compartida (sync); solo su stream async se espera.
                    awaitReply = connectionType > 0 || isStream && ((INamedTypeSymbol)method.ReturnType).Name == "IAsyncEnumerable";

                hostCode.Append("return ").Append(awaitReply ? "await " : null).Append(connectionType == 0 ? isStream ? "__context.Yield(" : "__context.Return(" : isStream ? "__context.EnumerateAsync(" : "__context.ReturnAsync(");

                if (isStream)
                {
                    if (connectionType > 1 && method.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "SourceCrafter.LiteSpeedLink.StreamAttribute") is { } streamAttr)
                    {
                        int batch = 0, delay = 0;
                        foreach (var arg in streamAttr.NamedArguments)
                        {
                            if (arg.Key == "Batch") batch = (int)arg.Value.Value!;
                            else if (arg.Key == "MaxDelayMs") delay = (int)arg.Value.Value!;
                        }

                        // Politica como tipo: el JIT pliega Items/MaxDelayTicks y elimina las ramas muertas.
                        string policy = batch <= 0 ? "global::SourceCrafter.LiteSpeedLink.Unbatched" : "__Policy_B" + batch + "_D" + Math.Max(delay, 0);

                        if (batch > 0 && emittedPolicies.Add(policy))
                            policyTypes.PrependLine(2, "private readonly struct ").Append(policy).Append(" : global::SourceCrafter.LiteSpeedLink.IStreamPolicy")
                                .Open()
                                .Line("public static int Items => ").Append(batch).Append(';')
                                .Line("public static long MaxDelayTicks => ").Append(delay <= 0 ? "long.MaxValue" : "global::System.Diagnostics.Stopwatch.Frequency * " + delay + " / 1000").Append(';')
                                .Close();

                        hostCode.Length -= 1;
                        hostCode.Append('<').Append(((INamedTypeSymbol)method.ReturnType).TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append(", ").Append(policy).Append(">(");
                    }

                    hostCode.Append("___result");
                }
                else if (outCount > 0)
                {
                    hostCode.Parens(resultExpression, outCount > 1);
                }
                else
                {
                    hostCode.Append("false");
                }

                hostCode.Append(awaitReply ? ").ConfigureAwait(false);" : ");").Close();

                operations.Add((serviceId, globalizedMethodName, opName, EndOperation(hostCode, signatureStart, signatureEnd, handlerReturnType, connectionType == 0)));
            }
        }

        // Despachador sin async ni try: las excepciones las convierte en Failed el servidor (Server.*), que ya rodea la llamada.
        // Memory sin ninguna operacion que espere responde sync (StartMemoryServer); si no, las sync se envuelven en new(...).
        bool syncMemory = connectionType == 0 && operations.All(o => o.Sync);
        string? wrapOpen = syncMemory ? null : "new(", wrapClose = syncMemory ? null : ")";

        hostCode.Blank().Line("private ").Append(syncMemory ? handlerReturnType : "global::System.Threading.Tasks.ValueTask<" + handlerReturnType + ">")
            .Append(" HandleRequestsAsync(long id, ").Append(context).Append(" __context, global::System.Threading.CancellationToken __token) => id switch")
            .Open();

        foreach (var (id, signature, name, sync) in operations)
            hostCode.Line().Append(id).Append(" => ").Append(sync ? wrapOpen : null).Append(name).Append("(__context, __token)").Append(sync ? wrapClose : null)
                .Append(", //Id for: [").Append(signature).Append(']');

        hostCode.Line("_ => ").Append(connectionType is 0 ? wrapOpen + "__context.NotFound()" + wrapClose : "__context.NotFoundAsync()")
            .Close(";");

        hostCode.Append(policyTypes).Close();

        if (syncMemory)
            hostCode.Replace("Server.StartMemoryServerAsync(", "Server.StartMemoryServer(");

        contribution.AddSource(hintName + ".host", hostCode.ToString());
    }

    /// <summary>
    /// [ServerCache]: acierto -> bytes guardados; fallo -> single-flight (un solo handler por peticion identica en curso,
    /// el resto espera sus bytes). Ningun lock abarca el await del handler.
    /// </summary>
    /// Deja abierto el cuerpo del try (dos bloques), que <see cref="CloseServerCache"/> cierra junto con el case.
    private static void OpenServerCache(CodeBuilder code, string suffix, string key)
    {
        code.Line("var __cache = __Cache").Append(suffix).Append(';')
            .Line("var __cacheKey = ").Append(key).Append(';')
            .Blank()
            .Open("if (!__cache.TryGet(__cacheKey, out var __cached))")
            .Line("if (!__cache.TryLead(__cacheKey, out var __flight))")
            .Line("__cached = await __flight.Task.WaitAsync(__token).ConfigureAwait(false);", 1)
            .Open("else try");
    }

    /// <summary>
    /// Cierra el metodo de una operacion: en Memory, si su cuerpo no espera nada, pasa a sync (sus return ya son
    /// ResponseStatus) y el despachador no paga maquina de estados por ella. Devuelve si quedo sync.
    /// </summary>
    // ponytail: se detecta por el texto emitido ("await "); los nombres que se emiten (metodos/pipelines) no lo contienen.
    private static bool EndOperation(CodeBuilder code, int signatureStart, int signatureEnd, string returnType, bool canSync)
    {
        if (!canSync || code.ToString(signatureEnd, code.Length - signatureEnd).IndexOf("await ", StringComparison.Ordinal) >= 0)
            return false;

        code.Remove(signatureStart, signatureEnd - signatureStart).Insert(signatureStart, "private " + returnType);
        return true;
    }

    /// <summary>Guarda los bytes tras los return processors; fallo o rechazo no se cachean (Fail es idempotente).</summary>
    private static void CloseServerCache(CodeBuilder code, string suffix, string result, bool canAwait)
    {
        code.TrimEnd().Blank()
            .Line("__cached = __Res").Append(suffix).Append('(').Append(result).Append(");")
            .Line("__cache.Complete(__cacheKey, __flight, __cached);")
            .Close()
            .Line("catch (global::System.Exception __cacheError)")
            .Open().Line("__cache.Fail(__cacheKey, __flight, __cacheError);").Line("throw;").Close()
            .Line("finally")
            .Open().Line("__cache.Fail(__cacheKey, __flight, null);").Close()
            .Close()
            .Blank()
            .Line("return ").Append(canAwait ? "await __context.ReturnRawAsync(__cached).ConfigureAwait(false);" : "__context.ReturnRaw(__cached);")
            .Close();
    }

    /// <summary>
    /// Emite la lectura de un servicio a traves del miembro que el generador de DI le asigno.
    ///
    /// <para>
    /// El contenedor decide por su cuenta si el resolver sale como propiedad o como metodo, asi
    /// que los parentesis se ponen segun <c>MemberIsMethodShaped</c> y no por el lifetime: un
    /// transient sincrono es una propiedad, y escribirle <c>()</c> no compilaba.
    /// </para>
    /// </summary>
    private static void AppendResolution(CodeBuilder code, ServiceInfo service, string provider = "")
    {
        code.Append(provider).Append(service.MemberName);

        if (service.MemberIsMethodShaped) code.Append("()");
    }

    /// <summary>
    /// Empareja un parametro anotado con su registro, leyendo del atributo solo el lifetime y la
    /// clave. Todo lo demas lo aporta la tabla ya resuelta por el generador de DI.
    /// </summary>
    private static bool TryResolveAnnotatedParameter(
        Dictionary<(PartialLifetime, string, string), ServiceInfo> services,
        AttributeData paramAttr,
        IParameterSymbol param,
        out ServiceInfo service)
    {
        service = null!;

        if (paramAttr.AttributeClass?.ToDisplayString() is not { } attrName) return false;

        var lifetime = attrName switch
        {
            "SourceCrafter.DependencyInjection.Attributes.SingletonAttribute" => PartialLifetime.Singleton,
            "SourceCrafter.DependencyInjection.Attributes.ScopedAttribute" => PartialLifetime.Scoped,
            "SourceCrafter.DependencyInjection.Attributes.TransientAttribute" => PartialLifetime.Transient,
            _ => (PartialLifetime?)null
        };

        if (lifetime is not { } resolvedLifetime) return false;

        var key = paramAttr.ConstructorArguments is [{ Value: string declaredKey }, ..]
            ? declaredKey
            : "";

        var exportTypeFullName = param.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Un parametro sin clave explicita puede estar desambiguado por su propio nombre, que es
        // la misma regla que aplica el generador de DI (SCDI17).
        return services.TryGetValue((resolvedLifetime, exportTypeFullName, key), out service!)
            || (key.Length is 0
                && services.TryGetValue((resolvedLifetime, exportTypeFullName, param.Name), out service!));
    }

    internal const string
            BaseAttributesNS = "SourceCrafter.DependencyInjection.Attributes",
            GlobalBaseAttributeNS = $"global::{BaseAttributesNS}",
            ServiceContainerFullTypeName = $"{BaseAttributesNS}.ServiceContainerAttribute",
            CancelTokenFQMetaName = "System.Threading.CancellationToken",
            EnumFQMetaName = "global::System.Enum",
            KeyParamName = "key",
            NameFormatParamName = "nameFormat",
            SourceParamName = "source",
            ImplParamName = "impl",
            IfaceParamName = "iface",
            SingletonAttr = $"{GlobalBaseAttributeNS}.SingletonAttribute",
            ScopedAttr = $"{GlobalBaseAttributeNS}.ScopedAttribute",
            TransientAttr = $"{GlobalBaseAttributeNS}.TransientAttribute",
            DependencyAttr = $"{GlobalBaseAttributeNS}.DependencyAttribute",
            ServiceContainerAttr = $"global::{ServiceContainerFullTypeName}";

}
