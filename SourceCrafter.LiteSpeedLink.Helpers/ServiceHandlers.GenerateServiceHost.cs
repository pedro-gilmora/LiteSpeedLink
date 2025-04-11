using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using SourceCrafter.DependencyInjection;
//using SourceCrafter.Helpers;

using System;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

using Dependencies = SourceCrafter.DependencyInjection.Dependencies;

namespace SourceCrafter.LiteSpeedLink
{
    public partial class ServiceHandlersGenerator
    {
        private const string IServiceUnit = "global::SourceCrafter.LiteSpeedLink.IServiceUnit";

        private static void GenerateServiceHost(SourceProductionContext sourceGenCtx, Compilation compilation, in (INamedTypeSymbol, int) serviceHostDesc)
        {
            var identity = compilation.Assembly.Identity;
            var (serviceHost, connectionType) = serviceHostDesc;

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

            string? typeName = serviceHost.ToTypeNameFormat();
            var (certParam, certArg) = connectionType > 0
                    ? ($@",
        global::System.Security.Cryptography.X509Certificates.X509Certificate2{(connectionType == 1 ? "?" : null)} certificate{(connectionType == 1 ? " = default" : null)}", @", 
            certificate")
                    : (null, null);

            var (context, startMethod, returnType, disposableInterface, asyncKeyword, awaitKeyword, asyncPrefix) = connectionType switch
            {
                2 => ("global::SourceCrafter.LiteSpeedLink.RequestContext",
                      "StartQuicServerAsync",
                      "global::System.Threading.Tasks.ValueTask<global::System.Net.Quic.QuicListener>",
                      "global::System.IAsyncDisposable",
                      "async ",
                      "await ",
                      "Async"),
                1 => ("global::SourceCrafter.LiteSpeedLink.RequestContext",
                      "StartTcpServer",
                      "global::System.Net.Sockets.TcpListener",
                      "global::System.Disposable",
                      null,
                      null,
                      null),
                _ => ("global::SourceCrafter.LiteSpeedLink.UdpRequestContext",
                      "StartUdpServer",
                      "global::System.Net.Sockets.UdpClient",
                      "global::System.IDisposable",
                      null,
                      null,
                      null),
            };

            var handlerReturnType = connectionType is 0 ? "int" : "global::System.IO.Pipelines.FlushResult";

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
            .AddFilter(""").Append(serviceHost.ToGlobalNamespaced().Replace("global::", "")).Append(@""", global::Microsoft.Extensions.Logging.LogLevel.Debug)");

                if (isMsConsoleLoggerInstalled)
                {
                    hostCode.Append(@"
            .AddConsole()");
                }

                hostCode.Append(@";
    });
");
            }

            string? disposability = null;

            hostCode.Append(@"
    public static ").Append(asyncKeyword).Append(returnType).Append(@" Start").Append(asyncPrefix).Append(@"(
        int port").Append(certParam).Append(@",
        global::System.Threading.CancellationToken cancelToken = default)
	{
        var provider = new ").Append(typeName).Append(@"();
		return ").Append(awaitKeyword).Append(@"global::SourceCrafter.LiteSpeedLink.Server.").Append(startMethod).Append(@"(
            port, 
            provider.HandleRequestsAsync, 
            () => /*[[DISPOSABILITY]]*/").Append(certArg).Append(@", 
            cancelToken);
	}
    	
    private async global::System.Threading.Tasks.ValueTask<").Append(handlerReturnType).Append(@"> HandleRequestsAsync(
        long id,
        ").Append(context).Append(@" __context,
        global::System.Threading.CancellationToken __token)
    {
        switch(id)
        {");

            string
                fullContainerTypeName = serviceHost.ToGlobalNamespaced(),
                nsMeta = serviceHost.ContainingNamespace.ToMetadataLongName(),
                typeMeta = serviceHost.ToMetadataLongName(),
                justTypeMeta = nsMeta.Length > 0 ? typeMeta.Replace(nsMeta, "") : typeMeta,
                hintName = nsStr.Length > 0 ? nsStr + "." + justTypeMeta : justTypeMeta;

            string? serviceCommaSep = default;

            foreach (var attr in serviceHost.GetAttributes())
            {
                var attrCls = attr.AttributeClass!;
                bool isExternal = false;

                if (!compilation.GetSemanticModel(attr.ApplicationSyntaxReference!.GetSyntax().SyntaxTree).TryGetDependencyInfo(
                        attr,
                        ref isExternal,
                        "",
                        null,
                        out var lifetime,
                        out var finalType,
                        out var iFaceType,
                        out var implType,
                        out var factory,
                        out var factoryKind,
                        out var key,
                        out var nameFormat,
                        out var defaultParamValues,
                        out var isCached,
                        out var _disposability,
                        out var isServiceAsync,
                        out var attrSyntax,
                        out var isValid) 
                    || !isValid
                    || finalType.ToGlobalNamespaced() is not { } fullDependencyTypeName
                    || Dependencies.GetDependency(identity, fullContainerTypeName, lifetime, fullDependencyTypeName, key) is not { } dependency) continue;

                var model = compilation.GetSemanticModel(implType.DeclaringSyntaxReferences[0].SyntaxTree);
                bool useIface = iFaceType is not null;
                var membersSource = finalType;

                foreach (var member in membersSource.GetMembers())
                {
                    if (!(member is IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false } method))
                    {
                        continue;
                    }

                    string
                        methodName = method.ToNameOnly(),
                        globalizedMethodName = method.ToGlobalNamespaced();

                    bool asyncPropagation = (method.ReturnType.Name.EndsWith("Task")
                            && method.ReturnType.ToString().StartsWith("System.Threading.Tasks"))
                            || lifetime is Lifetime.Scoped;

                    var (_async, _await) = asyncPropagation
                            ? ("async ", "await ")
                            : default;

                    var serviceId = GetServiceId(globalizedMethodName);

                    hostCode.Append(Interlocked.Exchange(ref serviceCommaSep, ",")).Append(@"
        case ").Append(serviceId).Append(@": //Id for: [").Append(globalizedMethodName).Append(@"]

            ");
                    var provider = "";

                    if (lifetime is Lifetime.Scoped)
                    {
                        if (dependency.ContainerDisposability is Disposability.AsyncDisposable)
                        {
                            if (disposability is null)
                            {
                                disposability = "provider.DisposeAsync().AsTask().GetAwaiter().GetResult()";
                                hostCode.Replace("/*[[DISPOSABILITY]]*/ ", disposability);
                            }
                            hostCode.Append("await using ");
                        }
                        else if (dependency.ContainerDisposability is Disposability.Disposable)
                        {
                            if (disposability is null)
                            {
                                disposability = "provider.Dispose()";
                                hostCode.Replace("/*[[DISPOSABILITY]]*/ ", disposability);
                            }
                            hostCode.Append("using ");
                        }
                        else if (disposability is null)
                        {
                            disposability = "{}";
                            hostCode.Replace("/*[[DISPOSABILITY]]*/", disposability);
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

                    bool separateParams = false,
                        separateRequestParams = false,
                        separateResponseParams = false,
                        separateRequestTypes = false,
                        cancelTokenIsSet = false;

                    if (returnsType)
                    {
                        outCount++;
                        resultExpression += () =>
                        {
                            hostCode.Append("___result");

                            separateResponseParams = true;
                        };
                    }

                    if (!hasEmptyParams)
                    {
                        foreach (var param in method.Parameters)
                        {
                            var paramType = param.Type.ToGlobalNamespaced();

                            foreach (var paramAttr in param.GetAttributes())
                            {
                                var pAttrCls = attr.AttributeClass!;
                                bool pIsExternal = false;

                                if (!compilation.GetSemanticModel(paramAttr.ApplicationSyntaxReference!.GetSyntax().SyntaxTree).TryGetDependencyInfo(
                                    attr,
                                    ref pIsExternal,
                                    param.Name,
                                    param.Type,
                                    out var pLifetime,
                                    out var pFinalType,
                                    out var pIFaceType,
                                    out var pImplType,
                                    out var pFactory,
                                    out var pFactoryKind,
                                    out var pKey,
                                    out var pNameFormat,
                                    out var pDefaultParamValues,
                                    out var pIsCached,
                                    out var pDisposability,
                                    out var pIsAsync,
                                    out var pAttrSyntax,
                                    out var pIsValid) || !isValid)

                                    continue; //check next attribute

                                if (Dependencies.GetDependency(identity, fullContainerTypeName, pLifetime, pFinalType.ToGlobalNamespaced(), pKey) is not { } paramDependency) 
                                    goto NEXT_PARAM;


                                invokeParams += () =>
                                    {
                                        if (Exchange(ref separateParams)) hostCode.Append(", ");

                                        hostCode.Append(dependency.ResolverMethodName);

                                        hostCode.Append("(");

                                        if (paramAttr.ConstructorArguments is [{ Value: string name }])
                                        {
                                            hostCode.Append(@"""").Append(name).Append(@"""");
                                        }

                                        hostCode.Append(")");
                                    };

                                goto NEXT_PARAM;
                            }

                            switch (param.Type.ToGlobalNonGenericNamespace())
                            {
                                case "global::Microsoft.Extensions.Logging.ILogger"
                                    when isMsLoggerInstalled && param.Type is INamedTypeSymbol { IsGenericType: true, TypeArguments: [{ } genericLogger] }:

                                    invokeParams += () =>
                                    {
                                        (Exchange(ref separateParams) ? hostCode.Append(", ") : hostCode)
                                            .Append("___loggerFactory.CreateLogger<").Append(genericLogger.ToGlobalNamespaced()).Append(">()");
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

                            if (param.RefKind != RefKind.Out)
                            {
                                //Register params to deconstruct request from deserialization
                                requestDeconstruct += () =>
                                    (Exchange(ref separateRequestParams) ? hostCode.Append(", ") : hostCode).Append(param.Name);

                                //Register types for request deserialization
                                requestTypes += () =>
                                    (Exchange(ref separateRequestTypes) ? hostCode.Append(", ") : hostCode).Append(paramType);
                            }


                            //Register params usage and in/out/ref tuple types
                            switch (param.RefKind)
                            {
                                case RefKind.In:

                                    outCount++;

                                    invokeParams += () =>
                                        (Exchange(ref separateParams) ? hostCode.Append(", ") : hostCode)
                                            .Append("in ").Append(param.Name);

                                    break;

                                case RefKind.Ref or RefKind.RefReadOnly or RefKind.RefReadOnlyParameter:

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

                    if (requestParamsCount > 0)
                    {
                        if (requestTypes != null)
                        {
                            hostCode.Append(@"var ");

                            if (requestParamsCount > 1)
                            {
                                hostCode.Append("(");

                                requestDeconstruct!.Invoke();

                                hostCode.Append(") = __context.Get<(");

                                requestTypes!.Invoke();

                                hostCode.Append(@")>();

            ");
                            }
                            else if (requestDeconstruct != null)
                            {
                                requestDeconstruct();

                                hostCode
                                    .Append(" = __context.Get<");

                                requestTypes!.Invoke();

                                hostCode.Append(@">();

            ");
                            }
                        }
                    }

                    if (returnsType)
                    {
                        hostCode.Append(@"var ___result = ");
                    }

                    bool shouldParenthizeExpression = dependency.IsAsync || (lifetime is Lifetime.Transient && !dependency.IsCached);

                    if (dependency.IsAsync) hostCode.Append(_await);

                    if (shouldParenthizeExpression) hostCode.Append('(');

                    if(isServiceAsync) hostCode.Append(_await);

                    hostCode.Append(provider).Append(dependency.ResolverMethodName);

                    if (shouldParenthizeExpression) hostCode.Append(')');

                    hostCode.Append(".").Append(methodName).Append('(');

                    invokeParams?.Invoke();

                    hostCode.Append(@");

            return await __context.ReturnAsync(");

                    if (outCount > 0)
                    {
                        if (outCount == 1)
                        {
                            resultExpression!();

                            hostCode.Append(", ");
                        }
                        else
                        {
                            hostCode.Append("(");

                            resultExpression!();

                            hostCode.Append("), ");
                        }
                    }

                    hostCode.Append(@"__token);
");
                }
            }

            hostCode.Append(@"

            default:

                return await __context.ReturnAsync(false);
        }
    }
}");

            sourceGenCtx.AddSource(hintName + ".host.cs", hostCode.ToString());
        }

        public static long GetServiceId(string input)
        {
            using MD5 md5 = MD5.Create();
            Guid id = new(md5.ComputeHash(Encoding.Default.GetBytes(input)));

            return BitConverter.ToInt64(id.ToByteArray(), 8);
        }
    }
}
