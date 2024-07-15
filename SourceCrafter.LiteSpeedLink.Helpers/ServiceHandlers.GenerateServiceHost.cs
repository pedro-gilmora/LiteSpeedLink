using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

using SourceCrafter.Helpers;

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SourceCrafter.LiteSpeedLink
{
    public partial class ServiceHandlersGenerator
    {
        private static void GenerateServiceHost(SourceProductionContext sourceGenCtx, Compilation compilation, (INamedTypeSymbol, int) serviceHostDesc)
        {
            var (serviceHost, connectionType) = serviceHostDesc;

            bool 
                isMsLoggerInstalled = compilation.GetTypeByMetadataName("Microsoft.Extensions.Logging.ILogger") != null,
                isMsConsoleLoggerInstalled = compilation.GetTypeByMetadataName("Microsoft.Extensions.Logging.ConsoleLoggerExtensions") != null;
            
            StringBuilder hostCode = new();

            string nsStr = "";

            string? typeName = serviceHost.ToTypeNameFormat();
            var (certParam, certArg) = connectionType > 0
                    ? ($@",
        global::System.Security.Cryptography.X509Certificates.X509Certificate2{(connectionType == 1 ? "?" : null)} certificate{(connectionType == 1 ? " = default" : null)}", ", certificate")
                    : (null, null);

            var (context, startMethod, returnType, disposableInterface, asyncKeyword, awaitKeyword, asyncPrefix) = connectionType switch
            {
                2 => ("global::SourceCrafter.LiteSpeedLink.RequestContext<" + typeName + ">", 
                      "StartQuicServerAsync", 
                      "global::System.Threading.Tasks.ValueTask<global::System.Net.Quic.QuicListener>", 
                      "global::System.IAsyncDisposable",
                      "async ",
                      "await ",
                      "Async"),
                1 => ("global::SourceCrafter.LiteSpeedLink.RequestContext<" + typeName + ">",
                      "StartTcpServer", 
                      "global::System.Net.Sockets.TcpListener", 
                      "global::System.Disposable",
                      null,
                      null,
                      null),
                _ => ("global::SourceCrafter.LiteSpeedLink.UdpRequestContext<" + typeName + ">",
                      "StartUdpServer", 
                      "global::System.Net.Sockets.UdpClient", 
                      "global::System.IDisposable",
                      null,
                      null,
                      null),
            };

            var handlerType = context.Replace("Context<", "Handler<");

            if (serviceHost.ContainingNamespace is { IsGlobalNamespace: false } ns)
            {
                hostCode.Append("namespace ").Append(nsStr = ns.ToDisplayString()).AppendLine(@";");
            }

            hostCode.Append(@"
public partial class ").Append(typeName).Append(@"
{
    public static ").Append(asyncKeyword).Append(returnType).Append(@" Start").Append(asyncPrefix).Append(@"(
        int port").Append(certParam).Append(@",
        global::System.Threading.CancellationToken cancelToken = default)
	{
		return ")
                .Append(awaitKeyword)
                .Append(@"global::SourceCrafter.LiteSpeedLink.Server.")
                .Append(startMethod)
                .Append(@"(port, new ").Append(typeName).Append("(), handlers").Append(certArg).Append(@", cancelToken);
	}
    	
    private static readonly 
        global::System.Collections.Generic.Dictionary<int, ").Append(handlerType).Append(@"> handlers = 
            new global::System.Collections.Generic.Dictionary<int, ").Append(handlerType).Append(@">
    {");

            string
                fullTypeName = serviceHost.ToGlobalNamespaced(),
                nsMeta = serviceHost.ContainingNamespace.ToMetadataLongName(),
                typeMeta = serviceHost.ToMetadataLongName(),
                justTypeMeta = nsMeta.Length > 0 ? typeMeta.Replace(nsMeta, "") : typeMeta,
                hintName = nsStr.Length > 0 ? nsStr + "." + justTypeMeta : justTypeMeta;

            string? serviceCommaSep = default;

            foreach (var attr in serviceHost.GetAttributes())
            {
                if (attr.AttributeClass is INamedTypeSymbol
                    {
                        Name: ("Singleton" or "Transient" or "Scoped" or "SingletonAttribute" or "TransientAttribute" or "ScopedAttribute") and string kind,
                        TypeArguments: { IsDefaultOrEmpty: false, Length: (1 or 2) and int count } args
                    })
                {
                    INamedTypeSymbol impl;
                    ReadOnlySpan<INamedTypeSymbol> iFaces;
                    bool useIface;

                    if (count == 1 && args[0] is INamedTypeSymbol first
                        && first.AllInterfaces.Where(i =>
                             i.Interfaces.Any(ii => ii.ToGlobalNamespaced() == "global::SourceCrafter.LiteSpeedLink.IServiceUnit")).ToImmutableArray().AsSpan() is { Length: > 0 } founds)
                    {
                        impl = first;
                        iFaces = founds;
                        useIface = false;
                    }

                    else if (count == 2 && args[0] is INamedTypeSymbol first2
                        && first2.AllInterfaces.FirstOrDefault(i =>
                             i.Interfaces.Any(ii => ii.ToGlobalNamespaced() == "global::SourceCrafter.LiteSpeedLink.IServiceUnit")) is { } founds2
                        && args[1].AllInterfaces.Contains(first2, SymbolEqualityComparer.Default))
                    {
                        impl = (INamedTypeSymbol)args[1];
                        iFaces = new[] { first2 };
                        useIface = true;
                    }
                    else
                    {
                        continue;
                    }

                    var model = compilation.GetSemanticModel(impl.DeclaringSyntaxReferences[0].SyntaxTree);

                    foreach (var iFace in iFaces)
                    {
                        var fullInterfaceName = useIface ? iFace.ToGlobalNamespaced() : impl.ToGlobalNamespaced();

                        foreach (var member in iFace.GetMembers())
                        {
                            if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false } method)
                            {
                                string
                                    methodName = method.ToNameOnly(),
                                    globalizedMethodName = method.ToMinimalDisplayString(model, 0);

                                bool asyncPropagation = (method.ReturnType.Name.EndsWith("Task")
                                        && method.ReturnType.ToString().StartsWith("System.Threading.Tasks"))
                                        || kind.StartsWith("Scoped");

                                var (_async, _await) = asyncPropagation
                                        ? ("async ", "await ")
                                        : default;

                                var serviceId = GetServiceId(globalizedMethodName);

                                hostCode.Append(Interlocked.Exchange(ref serviceCommaSep, ",")).Append(@"
        {
            ").Append(serviceId).Append(@", //Id for: [").Append(globalizedMethodName).Append(@"]
            ").Append(_async).Append(@"(__context, __token) =>
            {
                ");
                                var providerRef = "__context.Provider.GetService<";

                                if (kind.StartsWith("Scoped"))
                                {
                                    hostCode.Append(@"using var ___scope = __context.Provider.CreateScope();

                ");

                                    providerRef = "___scope.GetService<";
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
                                            switch (paramAttr.AttributeClass?.ToGlobalNamespaced())
                                            {
                                                case "global::SourceCrafter.LiteSpeedLink.ServiceAttribute":

                                                    invokeParams += () =>
                                                    {
                                                        (Exchange(ref separateParams) ? hostCode.Append(", ") : hostCode)
                                                            .Append(providerRef).Append(param.Type.ToGlobalNamespaced()).Append(">(");

                                                        if (paramAttr.ConstructorArguments is [{ Value: string name }])
                                                        {
                                                            hostCode.Append(@"""").Append(name).Append(@"""");
                                                        }

                                                        hostCode.Append(")");
                                                    };

                                                    goto NEXT_PARAM;
                                            }
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
                                        };

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

                                hostCode.Append(_await).Append(providerRef).Append(fullInterfaceName).Append(">().").Append(methodName).Append('(');

                                invokeParams?.Invoke();

                                hostCode.Append(@");

                return ").Append(_await).Append(@"__context.ReturnAsync(");

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
            }");
                            }
                        }

                    }
                }
            }

            hostCode.Append(@"
        }
    };
}");

            if (isMsLoggerInstalled)
            {
                hostCode
                    .Insert(0, @"using global::Microsoft.Extensions.Logging;
")
                    .Replace("[___MS_LOGGER_VAR_PLACEHOLDER___]", @$"var ___loggerFactory = global::Microsoft.Extensions.Logging.LoggerFactory.Create(static builder =>
        {{
            builder.
                .AddFilter(""Microsoft"", global::Microsoft.Extensions.Logging.LogLevel.Warning)
                .AddFilter(""System"", global::Microsoft.Extensions.Logging.LogLevel.Warning)
                .AddFilter(""{serviceHost.ToGlobalNamespaced().Replace("global::", "")}"", global::Microsoft.Extensions.Logging.LogLevel.Debug);{(isMsConsoleLoggerInstalled ? @"
#if DEBUG
            builder.AddConsole();
#endif" : null)}
        }});")
                    .Replace("[___MS_LOGGER_PARAM_PLACEHOLDER___]", @$"___loggerFactory, ")
                    .Replace("[___MS_LOGGER_ARG_PLACEHOLDER___]", @$"global::Microsoft.Extensions.Logging.ILoggerFactory ___loggerFactory, ");
            }
            else
            {
                hostCode
                    .Replace("[___MS_LOGGER_VAR_PLACEHOLDER___]", "")
                    .Replace("[___MS_LOGGER_PARAM_PLACEHOLDER___]", "")
                    .Replace("[___MS_LOGGER_ARG_PLACEHOLDER___]", "");
            }

            sourceGenCtx.AddSource(hintName + ".host.cs", hostCode.ToString());
        }

        public static int GetServiceId(string input)
        {
            const int fnvPrime = 16777619;
            const int fnvOffsetBasis = unchecked((int)2166136261);

            int hash = fnvOffsetBasis;
            foreach (byte c in Encoding.UTF8.GetBytes(input))
            {
                hash ^= c;
                hash *= fnvPrime;
            }

            return hash;
        }
    }
}
