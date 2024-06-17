using Microsoft.CodeAnalysis;

using SourceCrafter.Helpers;

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SourceCrafter.MemLink.Helpers
{
    public partial class ServiceHandlersGenerator
    {
        private static void GenerateServiceHost(SourceProductionContext context, Compilation compilation, INamedTypeSymbol serviceHost, System.Security.Cryptography.MD5 mD5)
        {
            StringBuilder hostCode = new();

            string nsStr = "";

            if (serviceHost.ContainingNamespace is { IsGlobalNamespace: false } ns)
            {
                hostCode.Append("namespace ").Append(nsStr = ns.ToDisplayString()).AppendLine(@";");
            }

            //[global::Jab.ServiceProvider]
            string typeName = serviceHost.ToTypeNameFormat();

            hostCode.Append(@"
public partial class ").Append(typeName).Append(@"
{
    public static async void ListenAsync(string acceptFromHost, int port) => 
        ListenAsync(global::System.Net.IPEndPoint.Parse($""{acceptFromHost}:{port}""));

    public static async void ListenAsync(global::System.Net.IPEndPoint acceptFromHost)
    {
        var cancellationTokenSource = new global::System.Threading.CancellationTokenSource();

        var token = cancellationTokenSource.Token;

        using var listener = await global::KcpTransport.KcpListener.ListenAsync(acceptFromHost, token);

        using ").Append(typeName).Append(@" ___services = new ").Append(typeName).Append(@"();

        while (!token.IsCancellationRequested)
        {
            try
            {
                ProcessClientMessageAsync(___services, await listener.AcceptConnectionAsync(token), token);
            }
            catch (global::System.Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }

    protected static async void ProcessClientMessageAsync(").Append(typeName).Append(@" ___services, global::KcpTransport.KcpConnection connection, global::System.Threading.CancellationToken token = default)
    {
        using (connection)
        using (var ___stream = await connection.OpenOutboundStreamAsync())
        try
        {
            READ_REQUEST: 
            
            global::System.Memory<byte> __bytes__ = new byte[36];

			await ___stream.ReadExactlyAsync(__bytes__, token);

			var (___requestlen, ___opCode) = global::MemoryPack.MemoryPackSerializer.Deserialize<(int, string)>(__bytes__.Span);

            switch (___opCode)
            {");

            string
                fullTypeName = serviceHost.ToGlobalNamespaced(),
                nsMeta = serviceHost.ContainingNamespace.ToMetadataLongName(),
                typeMeta = serviceHost.ToMetadataLongName(),
                justTypeMeta = nsMeta.Length > 0 ? typeMeta.Replace(nsMeta, "") : typeMeta,
                hintName = nsStr.Length > 0 ? nsStr + "." + justTypeMeta : justTypeMeta;

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
                             i.Interfaces.Any(ii => ii.ToGlobalNamespaced() == "global::SourceCrafter.MemLink.IServiceUnit")).ToImmutableArray().AsSpan() is { Length: > 0 } founds)
                    {
                        impl = first;
                        iFaces = founds;
                        useIface = false;
                    }

                    else if (count == 2 && args[0] is INamedTypeSymbol first2
                        && first2.AllInterfaces.FirstOrDefault(i =>
                             i.Interfaces.Any(ii => ii.ToGlobalNamespaced() == "global::SourceCrafter.MemLink.IServiceUnit")) is { } founds2
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
                                bool requireSyncImpl = false;

                                string
                                    methodName = method.ToNameOnly(),
                                    globalizedMethodName = method.ToMinimalDisplayString(model, 0);

                                var serviceId = GetServiceId(mD5, globalizedMethodName);

                                hostCode.Append(@"
                case """).Append(serviceId).Append(@""": //").Append(globalizedMethodName).Append(@"
                {
                    ");
                                var providerRef = "___services.GetService<";

                                if (kind.StartsWith("Scoped"))
                                {
                                    hostCode.Append(@"using var ___scope = ___services.CreateScope();

                    ");

                                    providerRef = "___scope.GetService<";
                                }

                                bool
                                    hasEmptyParams = method.Parameters.IsDefaultOrEmpty,
                                    returnsType = !method.ReturnsVoid,
                                    useMetadata = returnsType || !hasEmptyParams;

                                Action?
                                    invokeParams = null,
                                    resultExpression = () => hostCode.Append("global::SourceCrafter.MemLink.ResponseStatus.Success"),
                                    requestTypes = null,
                                    requestDeconstruct = null;

                                if (returnsType)
                                {
                                    resultExpression += () => hostCode.Append(", ").Append("___result");
                                }

                                int requestParamsCount = 0;

                                if (!hasEmptyParams)
                                {
                                    bool separateParams = false, separateRequestParams = false, separateRequestTypes = false;

                                    foreach (var param in method.Parameters)
                                    {
                                        var paramType = param.Type.ToGlobalNamespaced();

                                        foreach(var paramAttr in param.GetAttributes())
                                        {
                                            if(paramAttr.AttributeClass?.ToGlobalNamespaced() is "global::SourceCrafter.MemLink.ServiceAttribute")
                                            {
                                                invokeParams += () =>
                                                {
                                                    (UseComma(ref separateParams) ? hostCode.Append(", ") : hostCode)
                                                        .Append(providerRef).Append(param.Type.ToGlobalNamespaced()).Append(">(");

                                                    if (paramAttr.ConstructorArguments is [{ Value: string name }])
                                                    {
                                                        hostCode.Append(@"""").Append(name).Append(@"""");
                                                    }

                                                    hostCode.Append(")");
                                                };

                                                continue;
                                            }
                                        }

                                        if (paramType == cancelTokenFullTypeName)
                                        {
                                            invokeParams += () =>
                                               (UseComma(ref separateParams) ? hostCode.Append(", ") : hostCode)
                                                   .Append("token");

                                            continue;
                                        }

                                        requestParamsCount++;

                                        if (param.RefKind != RefKind.Out)
                                        {
                                            //Register params to deconstruct request from deserialization
                                            requestDeconstruct += () =>
                                                (UseComma(ref separateRequestParams) ? hostCode.Append(", ") : hostCode).Append(param.Name);

                                            //Register types for request deserialization
                                            requestTypes += () =>
                                                (UseComma(ref separateRequestTypes) ? hostCode.Append(", ") : hostCode).Append(paramType);
                                        }


                                        //Register params usage and in/out/ref tuple types
                                        switch (param.RefKind)
                                        {
                                            case RefKind.In:

                                                invokeParams += () =>
                                                   (UseComma(ref separateParams) ? hostCode.Append(", ") : hostCode)
                                                       .Append("in ").Append(param.Name);

                                                break;

                                            case RefKind.Ref or RefKind.RefReadOnly or RefKind.RefReadOnlyParameter:

                                                invokeParams += () =>
                                                    (UseComma(ref separateParams) ? hostCode.Append(", ") : hostCode)
                                                        .Append("ref ").Append(param.Name);

                                                resultExpression += () => hostCode.Append(", ").Append(param.Name);

                                                break;
                                            case RefKind.Out:

                                            requestParamsCount--;

                                                invokeParams += () =>
                                                    (UseComma(ref separateParams) ? hostCode.Append(", ") : hostCode)
                                                        .Append("out var ").Append(param.Name);

                                                resultExpression += () => hostCode.Append(", ").Append(param.Name);

                                                break;
                                            default:

                                                invokeParams += () =>
                                                    (UseComma(ref separateParams) ? hostCode.Append(", ") : hostCode)
                                                        .Append(param.Name);

                                                break;
                                        };
                                    }
                                }

                                if (requestParamsCount > 0)
                                {
                                    if (requestTypes != null)
                                    {

                                        hostCode.Append(@"
			        await ___stream.ReadExactlyAsync(__bytes__ = new byte[___requestlen], token);

                    var ");

                                        if (requestParamsCount > 1)
                                        {
                                            hostCode.Append("(");

                                            requestDeconstruct!.Invoke();

                                            hostCode.Append(") = global::MemoryPack.MemoryPackSerializer.Deserialize<(");

                                            requestTypes!.Invoke();

                                            hostCode.Append(@")>(__bytes__.Span);

                    ");
                                        }
                                        else if (requestDeconstruct != null)
                                        {
                                            requestDeconstruct();

                                            hostCode
                                                .Append(" = global::MemoryPack.MemoryPackSerializer.Deserialize<");

                                            requestTypes!.Invoke();

                                            hostCode.Append(@">(__bytes__.Span);

                    ");
                                        }
                                    }
                                }                                

                                if (returnsType)
                                {
                                    hostCode.Append(@"var ___result = ");
                                }

                                if (method.IsAsync || method.ReturnType.Name.EndsWith("Task"))
                                {
                                    hostCode.Append("await ");
                                }

                                //Call service

                                hostCode.Append(providerRef).Append(fullInterfaceName).Append(">().").Append(methodName).Append('(');

                                invokeParams?.Invoke();

                                hostCode.Append(@");

                    __bytes__ = global::MemoryPack.MemoryPackSerializer.Serialize(");

                                if (!hasEmptyParams || returnsType)
                                {
                                    hostCode.Append("(");

                                    resultExpression();

                                    hostCode.Append(")");
                                }
                                else
                                {
                                    resultExpression();
                                }

                                hostCode.Append(@");

                    await ___stream.WriteAsync(__bytes__, token);

                    goto READ_REQUEST;
                }
");
                            }
                        }

                    }
                }
            }

            hostCode.Append(@"
                default:

                    await ___stream.WriteAsync(global::MemoryPack.MemoryPackSerializer.Serialize((global::SourceCrafter.MemLink.ResponseStatus.NotFound, string.Empty)), cancellationToken: token);

                    goto READ_REQUEST;
            }
        }
        catch (global::KcpTransport.KcpDisconnectedException)
        {
            Console.WriteLine($""Disconnected, Id:{connection.ConnectionId}"");
        }
        catch (global::System.Exception e)
        {
            await ___stream.WriteAsync(global::MemoryPack.MemoryPackSerializer.Serialize((global::SourceCrafter.MemLink.ResponseStatus.Failed, e.ToString())), cancellationToken: token);
        }
    }
}");

            context.AddSource(hintName + ".host.cs", hostCode.ToString());

            static bool UseComma(ref bool useParamCommaSeparator)
            {
                return ((useParamCommaSeparator, _) = (true, useParamCommaSeparator)).Item2;
            }
        }

        private static string GetServiceId(MD5 mD5, string globalizedMethodName)
        {
            return new Guid(mD5.ComputeHash(Encoding.UTF8.GetBytes(globalizedMethodName))).ToString("N");
        }
    }
}
