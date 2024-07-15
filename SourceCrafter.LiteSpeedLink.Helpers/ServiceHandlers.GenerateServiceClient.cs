using Microsoft.CodeAnalysis;

using SourceCrafter.Helpers;

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;

namespace SourceCrafter.LiteSpeedLink
{
    public partial class ServiceHandlersGenerator
    {
        const string cancelTokenFullTypeName = "global::System.Threading.CancellationToken";

        const SymbolDisplayParameterOptions paramsOptions =
            SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeName |
            SymbolDisplayParameterOptions.IncludeDefaultValue;

        public static string GetString(IParameterSymbol symbol)
        {
            return symbol.ToDisplayString(Extensions._globalizedNamespace.WithParameterOptions(paramsOptions));
        }

        private static void GenerateServiceClient(SourceProductionContext context, Compilation compilation, (INamedTypeSymbol, int) serviceClientDesc)
        {
            var (serviceClient, connectionType) = serviceClientDesc;

            StringBuilder clientCode = new();

            string nsStr = "";

            var (iDisposable, dispMethod) = connectionType is not 0
                ? ("IAsyncDisposable", @"public global::System.Threading.Tasks.ValueTask DisposeAsync()
    {
        return __connection.DisposeAsync();
    }")
    : ("IDisposable", @"public void Dispose()
    {
        __connection.Dispose();
    }");
            var connTypeName = connectionType switch
            {
                0 => "Udp",
                1 => "Tcp",
                _ => "Quic"
            };

            //[global::Jab.ServiceProvider]
            clientCode.Append(@"using SourceCrafter.LiteSpeedLink.Client;
");

            if(serviceClient.ContainingNamespace is { IsGlobalNamespace: false } nss)
            {
                clientCode.Append(@"
namespace ").Append(nsStr = nss.ToDisplayString()).Append(@";
");
            }

            string typeShortName = serviceClient.ToTypeNameFormat();

            clientCode.Append(@"
public partial class ").Append(typeShortName).Append(@"
{
    private readonly global::SourceCrafter.LiteSpeedLink.Client.IConnection __connection;

    private readonly static object _lock = new object();

    public ").Append(typeShortName).Append(@"(string hostname, int port)
    {
        __connection = new global::System.Net.DnsEndPoint(hostname, port).As").Append(connTypeName).Append(@"Connection();
        
        lock (_lock)
        {
            (_disposables ??= new global::System.Collections.Generic.List<object>()).Add(__connection);
        }
    }");

            string
                typeName = serviceClient.ToGlobalNamespaced(),
                nsMeta = serviceClient.ContainingNamespace.ToMetadataLongName(),
                typeMeta = serviceClient.ToMetadataLongName(),
                justTypeMeta = nsMeta.Length > 0 ? typeMeta.Replace(nsMeta, "") : typeMeta,
                hintName = nsStr.Length > 0 ? nsStr + "." + justTypeMeta : justTypeMeta;

            foreach (var iFace in
                serviceClient.AllInterfaces.Where(i =>
                     i.Interfaces.Any(ii => ii.ToGlobalNamespaced() == "global::SourceCrafter.LiteSpeedLink.IServiceUnit")).ToImmutableArray().AsSpan())
            {
                var fullTypeName = iFace.ToGlobalNamespaced();

                var model = compilation.GetSemanticModel(iFace.DeclaringSyntaxReferences[0].SyntaxTree);

                foreach (var member in iFace.GetMembers())
                {
                    if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false } method)
                    {
                        bool
                            hasEmptyParams = method.Parameters.IsDefaultOrEmpty,
                            isTask = method.ReturnType.ToGlobalNonGenericNamespace().AsSpan() is ['g', 'l', 'o', 'b', 'a', 'l', ':', ':', 'S', 'y', 's', 't', 'e', 'm', '.', 'T', 'h', 'r', 'e', 'a', 'd', 'i', 'n', 'g', '.', 'T', 'a', 's', 'k', 's', '.', .., 'T', 'a', 's', 'k'],
                            hasReturnType = !method.ReturnsVoid || (isTask && method.ReturnType is INamedTypeSymbol { TypeArguments.IsDefaultOrEmpty: true }),
                            hasResultOrParams = hasReturnType || !hasEmptyParams,
                            nameEndsWithTask = method.ReturnType.Name is [.., 'T', 'a', 's', 'k'],
                            needsCancelToken = true,
                            useReqTypesComma = false;

                        var returnType = nameEndsWithTask && method.ReturnType is INamedTypeSymbol { TypeArguments: [{ } type] }
                            ? type
                            : method.ReturnType;

                        string
                            methodName = method.ToNameOnly(),
                            globalizedMethodName = method.ToMinimalDisplayString(model, 0),
                            returnFullTypeName = returnType.ToGlobalNonGenericNamespace(),
                            cancelTokenParam = null!,
                            opMethod = hasReturnType
                                ? returnType.ToGlobalNonGenericNamespace() switch
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
                                var paramType = param.Type.ToGlobalNamespaced();

                                switch (param.RefKind)
                                {
                                    case RefKind.Ref or RefKind.RefReadOnly or RefKind.RefReadOnlyParameter:

                                        methodParams += () =>
                                        {
                                            if (Exchange(ref paramsComma)) clientCode.Append(", ");

                                            clientCode.Append(GetString(param));
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
                                            .Append(param.Type.ToGlobalNamespaced());

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

                                            clientCode.Append(GetString(param));
                                        };

                                        if (cancelTokenParam == null && paramType == cancelTokenFullTypeName)
                                        {
                                            cancelTokenParam = param.ToNameOnly();

                                            needsCancelToken = false;

                                            continue;
                                        }

                                        inCount++;

                                        requestTypes += () =>
                                            (Exchange(ref useReqTypesComma)
                                               ? clientCode.Append(", ")
                                               : clientCode)
                                            .Append(param.Type.ToGlobalNamespaced());

                                        requestParams += () =>
                                            (Exchange(ref asyncParamsComma)
                                               ? clientCode.Append(", ")
                                               : clientCode).Append(param.Name);

                                        break;
                                };
                            }
                        }

                        int serviceId = GetServiceId(globalizedMethodName);

                        clientCode.Append(@"

    public global::System.Threading.Tasks.ValueTask");

                        if (outCount > 0)
                        {
                            clientCode.Append("<");

                            if (outCount > 1)
                            {
                                clientCode.Append("(");

                                responseTypes!.Invoke();

                                clientCode.Append(")");
                            }
                            else
                            {
                                responseTypes!.Invoke();
                            }

                            clientCode.Append(">");
                        }

                        clientCode.AddSpace().Append(methodName);
                        
                        if(!methodName.EndsWith("Async"))
                        {
                            clientCode.Append("Async");
                        }

                        clientCode.Append("(");

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

                        clientCode.Append(@")
    {
        ");

                        clientCode
                            .Append("return __connection.")
                            .Append(opMethod)
                        .Append("Async");

                        bool useComma = false, closeTag = false;

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
                                    .Append(")");
                            }
                            else
                            {
                                clientCode
                                    .Append("<");

                                requestTypes!.Invoke();
                            }
                        }

                        if(responseTypes != null)
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
                                    clientCode.Append("(");

                                    responseTypes!.Invoke();

                                    clientCode.Append(")");
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
                                .Append(">");
                        }

                        clientCode
                            .Append("(")
                            .Append(serviceId)
                            .Append(", ");

                        if (inCount > 0)
                        {
                            if (inCount == 1)
                            {
                                requestParams?.Invoke();

                                clientCode.Append(", ");
                            }
                            else
                            {
                                clientCode.Append("(");

                                requestParams?.Invoke();

                                clientCode.Append("), ");
                            }
                        }

                        clientCode
                            .Append(cancelTokenParam)
                            .Append(@");
    }");

                        if (!isTask)
                        {
                            paramsComma = asyncParamsComma = useReqTypesComma = false;
                            
                            clientCode.Append(@"

    ");

                            clientCode
                                .Append(method.ToGlobalNamespaced())
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
                                    clientCode.Append(@"(");

                                    responseDeconstruct!.Invoke();

                                    clientCode.Append(@") = ");
                                }
                            }
                            else if(outCount == 1)
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

                            clientCode.Append(methodName);

                            if (!methodName.EndsWith("Async"))
                            {
                                clientCode.Append("Async");
                            }

                            clientCode
                                .Append("(");

                            if (inCount > 0)
                            {
                                requestParams?.Invoke();
                            }

                            clientCode
                                .Append(@").GetAwaiter().GetResult();");

                            if(hasReturnType && outCount > 1)
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
                        }
                    }
                }

            }

            clientCode.Append(@"
}");

            context.AddSource(hintName + ".client.cs", clientCode.ToString());
        }
        static bool Exchange(ref bool value)
        {
            return ((value, _) = (true, value)).Item2;
        }
    }
}

