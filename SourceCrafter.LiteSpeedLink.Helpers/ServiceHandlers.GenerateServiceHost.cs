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

public partial class ServiceHandlersGenerator
{
    private const string IServiceUnit = "global::SourceCrafter.LiteSpeedLink.IServiceUnit";

    private static void GenerateServiceHost(
        ServiceProviderInfo container,
        PartialContribution contribution,
        int connectionType,
        CancellationToken cancelToken)
    {
        var compilation = container.Compilation;
        var serviceHost = container.ContainerType;
        var model = container.SemanticModel;

        bool
            isMsLoggerInstalled = compilation.GetTypeByMetadataName("Microsoft.Extensions.Logging.ILogger") != null,
            isMsConsoleLoggerInstalled = compilation.GetTypeByMetadataName("Microsoft.Extensions.Logging.ConsoleLoggerExtensions") != null;

        StringBuilder hostCode = new();


        if (isMsLoggerInstalled)
        {
            hostCode.Append(@"using global::Microsoft.Extensions.Logging;

");
        }

        string nsStr = "";

        string? typeName = serviceHost.TypeNameFormat;
        var (certParam, certArg) = connectionType > 1
                ? ($@",
        global::System.Security.Cryptography.X509Certificates.X509Certificate2{(connectionType == 2 ? "?" : null)} certificate{(connectionType == 1 ? " = default" : null)}", @", 
            certificate")
                : (null, null);

        var (context, startMethod, returnType, disposableInterface, asyncKeyword, awaitKeyword, asyncSuffix) = connectionType switch
        {
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
                  "StartMemoryServer",
                  "global::System.IDisposable",
                  "global::System.IDisposable",
                  null,
                  null,
                  null),
        };

        var handlerReturnType = connectionType is 0 ? "byte[]" : "global::SourceCrafter.LiteSpeedLink.ResponseStatus";

        var handlerType = context.Replace("Context", "Handler");

        if (serviceHost.ContainingNamespace is { IsGlobalNamespace: false } ns)
        {
            hostCode.Append("namespace ").Append(nsStr = ns.ToDisplayString()).AppendLine(@";");
        }

        hostCode.Append(@"
public partial class ").Append(typeName).Append(@"
{");
        if (isMsLoggerInstalled)
        {
            hostCode.Append(@"
    static readonly global::Microsoft.Extensions.Logging.ILoggerFactory ___loggerFactory = global::Microsoft.Extensions.Logging.LoggerFactory.Create(static builder =>
    {
        builder
            .AddFilter(""Microsoft"", global::Microsoft.Extensions.Logging.LogLevel.Warning)
            .AddFilter(""System"", global::Microsoft.Extensions.Logging.LogLevel.Warning)
            .AddFilter(""").Append(serviceHost.GlobalNamespaced.Replace("global::", "")).Append(@""", global::Microsoft.Extensions.Logging.LogLevel.Debug)");

            if (isMsConsoleLoggerInstalled)
            {
                hostCode.Append(@"
            .AddConsole()");
            }

            hostCode.Append(@";
    });
");
        }

        hostCode.Append(@"
    public static ").Append(asyncKeyword).Append(returnType).Append(@" Start").Append(asyncSuffix).Append(@"(
        ").Append(connectionType is 0 ? "string memoryRpcName" : "int port").Append(certParam).Append(@",
        global::System.Threading.CancellationToken cancelToken = default)
	{
        var provider = new ").Append(typeName).Append(@"();
		return ").Append(awaitKeyword).Append(@"global::SourceCrafter.LiteSpeedLink.Server.").Append(startMethod).Append(@"(
            ").Append(connectionType is 0 ? "memoryRpcName" : "port").Append(@", 
            ").Append(connectionType > 1 ? "new __Handler(provider)" : "provider.HandleRequestsAsync").Append(@", 
            ");

        var onFinalizePoint = hostCode.Length;

        if (connectionType == 0)
            hostCode.Append(@",
            ").Append(1000);

        if (certArg != null)
            hostCode.Append(certArg);

		hostCode.Append(@", 
			cancelToken);
	}
");

		if (connectionType > 1)
			hostCode.Append(@"
	private readonly struct __Handler(").Append(typeName).Append(@" provider) : global::SourceCrafter.LiteSpeedLink.IRequestHandler
	{
		public global::System.Threading.Tasks.ValueTask<global::SourceCrafter.LiteSpeedLink.ResponseStatus> HandleAsync(long id, global::SourceCrafter.LiteSpeedLink.RequestContext ctx, global::System.Threading.CancellationToken token)
			=> provider.HandleRequestsAsync(id, ctx, token);
	}
");

		hostCode.Append(@"    	
	private ");

        int? handlerResultType = null;
        bool isMemoryAsync = false;

        if (connectionType > 0)
            hostCode.Append("async global::System.Threading.Tasks.ValueTask<").Append(handlerReturnType).Append('>');
        else
        {
            handlerResultType = hostCode.Length;
        }

        hostCode.Append(@" HandleRequestsAsync(
        long id,
        ").Append(context).Append(@" __context,
        global::System.Threading.CancellationToken __token)
    {
        try
        {");

        var switchStart = hostCode.Length;

        hostCode.Append(@"
        switch(id)
        {");

        var casesStart = hostCode.Length;

        int providerTypeId = SymbolEqualityComparer.Default.GetHashCode(serviceHost);

        string
            fullContainerTypeName = serviceHost.GlobalNamespaced,
            nsMeta = serviceHost.ContainingNamespace.MetadataLongName,
            typeMeta = serviceHost.MetadataLongName,
            justTypeMeta = nsMeta.Length > 0 ? typeMeta.Replace(nsMeta, "") : typeMeta,
            hintName = nsStr.Length > 0 ? nsStr + "." + justTypeMeta : justTypeMeta;

        //string? serviceCommaSep = default;

        // La disposability efectiva del contenedor la calcula el generador de DI: aqui ya no
        // se deduce servicio a servicio.
        var containerDisposability = container.ContainerDisposability;

        var services = IndexServices(container);

        StringBuilder policyTypes = new();
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

            if (!isMemoryAsync && (dependencyIsAsync || dependency.Disposability is PartialDisposability.AsyncDisposable)) isMemoryAsync = true;

            // El tipo expuesto ya viene resuelto en la propia fila: no hace falta el mapa
            // lateral que antes evitaba retener ISymbol.
            if (dependency.ExportType is not { } exportType) continue;

            // Los pipelines registrados se consumen desde los handlers; no son operaciones remotas.
            if (exportType is INamedTypeSymbol namedExport && TryGetStage(namedExport, out _)) continue;

            foreach (var member in exportType.GetMembers())
            {
                if (!(member is IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false } method))
                {
                    continue;
                }

                string
                    methodName = method.NameOnly,
                    globalizedMethodName = method.GlobalNamespaced;

                bool isMethodAsync = (method.ReturnType.Name.EndsWith("Task")
                        && method.ReturnType.ToDisplayString().StartsWith("System.Threading.Tasks"))
                        || dependency.Lifetime is PartialLifetime.Scoped;

                var (_async, _await) = isMethodAsync
                        ? ("async ", "await ")
                        : default;

                var serviceId = GetServiceId(globalizedMethodName);

                if (!isMemoryAsync && isMethodAsync) isMemoryAsync = true;



                //System.Console.WriteLine(@""Server received call to: ").Append(globalizedMethodName).Append(@"
    
                //    ServiceId: ").Append(serviceId).Append(@""");

                hostCode.Append(@"
        case ").Append(serviceId).Append(@": //Id for: [").Append(globalizedMethodName).Append(@"]
        {
            ");

                var provider = "";

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
                        hostCode.Append(@"var ___scope = CreateScope();

            ");
                        provider = "___scope.";
                    }
                }

                bool
                    hasEmptyParams = method.Parameters.IsDefaultOrEmpty,
                    returnsType = !method.ReturnsVoid,
                    useMetadata = returnsType || !hasEmptyParams;

                Action?
                    invokeParams = null,
                    resultExpression = null,
                    requestTypes = null,
                    requestDeconstruct = null;

                int requestParamsCount = 0, outCount = 0;
                List<ITypeSymbol> requestWireTypes = [];

                bool separateParams = false,
                    separateRequestParams = false,
                    separateResponseParams = false,
                    separateRequestTypes = false,
                    cancelTokenIsSet = false;

                var methodLocation = method.Locations.FirstOrDefault();
                var serverPost = GetStages(method.GetReturnTypeAttributes(), true, container, contribution, methodLocation);
                Action? applyPreStages = null;

                if (returnsType)
                {
                    ValidateChain(compilation, method.ReturnType, serverPost, null, methodName + " server response", contribution, methodLocation);

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

                            if (!isMemoryAsync && paramDependency.AsyncKind is not PartialAsyncKind.None) isMemoryAsync = true;

                            invokeParams += () =>
                                {
                                    if (Exchange(ref separateParams)) hostCode.Append(", ");

                                    AppendResolution(hostCode, paramDependency);
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

                        if (serverPre.Count > 0 && param.RefKind is not RefKind.None)
                        {
                            contribution.ReportDiagnostic(Diagnostic.Create(PipelineUnsupported, methodLocation, methodName + ": processors don't support ref/out/in parameters yet"));
                            serverPre.Clear();
                        }

                        if (param.RefKind != RefKind.Out)
                        {
                            var wireName = serverPre.Count > 0 ? "__w_" + param.Name : param.Name;
                            var wireType = serverPre.Count > 0 ? serverPre[0].In.GlobalNamespaced : paramType;
                            requestWireTypes.Add(serverPre.Count > 0 ? serverPre[0].In : param.Type);

                            if (serverPre.Count > 0)
                            {
                                ValidateChain(compilation, null, serverPre, param.Type, methodName + "(" + param.Name + ") server request", contribution, methodLocation);

                                applyPreStages += () => hostCode.Append(ApplyStages(wireName, serverPre, connectionType > 0, provider, "            ", param.Name));
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

                if (requestParamsCount > 0 && requestTypes != null && requestDeconstruct != null)
                {
                    var readerName = "__Req" + (serviceId < 0 ? "M" + (-serviceId) : serviceId.ToString());

                    EmitReader(policyTypes, readerName, requestWireTypes, "    ");

                    hostCode.Append("var ");

                    if (requestParamsCount > 1) hostCode.Append('(');

                    requestDeconstruct();

                    if (requestParamsCount > 1) hostCode.Append(')');

                    hostCode.Append(" = ").Append(readerName).Append(@"(__context.Body.Span);

            ");
                }

                applyPreStages?.Invoke();

                if (returnsType)
                {
                    hostCode.Append(@"var ___result = ");
                }

                bool shouldParenthizeExpression = dependencyIsAsync;

                if (dependencyIsAsync) hostCode.Append(_await);

                if (shouldParenthizeExpression) hostCode.Append('(');

                AppendResolution(hostCode, dependency, provider);

                if (shouldParenthizeExpression) hostCode.Append(')');

                hostCode.Append('.').Append(methodName).Append('(');

                invokeParams?.Invoke();

                hostCode.Append(@");

            ");

                if (returnsType && serverPost.Count > 0)
                    hostCode.Append(ApplyStages("___result", serverPost, connectionType > 0, provider, "            "));

                bool isStream = connectionType > 1 && returnsType
                    && method.ReturnType is INamedTypeSymbol { IsGenericType: true } rt
                    && rt.ConstructedFrom.ToDisplayString() is "System.Collections.Generic.IAsyncEnumerable<T>" or "System.Collections.Generic.IEnumerable<T>";

                hostCode.Append("return ").Append(connectionType == 0 ? "__context.Return(" : isStream ? "await __context.EnumerateAsync(" : "await __context.ReturnAsync(");

                if (isStream)
                {
                    if (method.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "SourceCrafter.LiteSpeedLink.StreamAttribute") is { } streamAttr)
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
                            policyTypes.Append(@"
    private readonly struct ").Append(policy).Append(@" : global::SourceCrafter.LiteSpeedLink.IStreamPolicy
    {
        public static int Items => ").Append(batch).Append(@";
        public static long MaxDelayTicks => ").Append(delay <= 0 ? "long.MaxValue" : "global::System.Diagnostics.Stopwatch.Frequency * " + delay + " / 1000").Append(@";
    }
");

                        hostCode.Length -= 1;
                        hostCode.Append('<').Append(((INamedTypeSymbol)method.ReturnType).TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append(", ").Append(policy).Append(">(");
                    }

                    hostCode.Append("___result");
                }
                else if (outCount > 0)
                {
                    if (outCount == 1)
                    {
                        resultExpression!();
                    }
                    else
                    {
                        hostCode.Append('(');

                        resultExpression!();

                        hostCode.Append(')');
                    }
                }
                else
                {
                    hostCode.Append("false");
                }

                hostCode.Append(@");
        }
");
            }
        }

        hostCode.Append(@"
        default:
            return ").Append(connectionType is 0 ? "__context.NotFound()" : "await __context.NotFoundAsync()").Append(@";");

        hostCode.Replace("\n        ", "\n            ", casesStart, hostCode.Length - casesStart);

        hostCode.Append(@"
        }");

        hostCode.Replace("\n        ", "\n            ", switchStart, hostCode.Length - switchStart);

        hostCode.Append(@"
        }
        catch (global::System.OperationCanceledException) when (__token.IsCancellationRequested)
        {
            throw;
        }
        catch (global::System.Exception __ex)
        {
            return ").Append(connectionType is 0 ? "__context.Fail(__ex)" : "await __context.FailAsync(__ex)").Append(@";
        }
    }
}");
        hostCode.Insert(hostCode.Length - 1, policyTypes.ToString());

        if (handlerResultType.HasValue)
        {
            hostCode.Insert(handlerResultType.Value, "byte[]");
        }

        hostCode.Insert(onFinalizePoint, containerDisposability switch
        {
            PartialDisposability.AsyncDisposable => "() => provider.DisposeAsync().GetAwaiter().GetResult()",
            PartialDisposability.Disposable => "() => provider.Dispose()",
            _ => "() => {}"
        });

        contribution.AddSource(hintName + ".host", hostCode.ToString());
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
    private static void AppendResolution(StringBuilder code, ServiceInfo service, string provider = "")
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

    /// <summary>
    /// Identificador estable de una operación. Debe producir el mismo valor en cliente y host,
    /// independientemente de la máquina, la cultura o el codepage ANSI por defecto.
    /// </summary>
    public static long GetServiceId(string input)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;

        var hash = offsetBasis;

        foreach (var b in Encoding.UTF8.GetBytes(input))
        {
            hash = (hash ^ b) * prime;
        }

        return unchecked((long)hash);
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
