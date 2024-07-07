using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

using SourceCrafter.Helpers;

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SourceCrafter.LiteSpeedLink.Helpers
{
    public partial class ServiceHandlersGenerator
    {
        private static void GenerateServiceHost(SourceProductionContext context, Compilation compilation, INamedTypeSymbol serviceHost, System.Security.Cryptography.MD5 mD5)
        {
            bool 
                isMsLoggerInstalled = compilation.GetTypeByMetadataName("Microsoft.Extensions.Logging.ILogger") != null,
                isMsConsoleLoggerInstalled = compilation.GetTypeByMetadataName("Microsoft.Extensions.Logging.ConsoleLoggerExtensions") != null;
            
            StringBuilder hostCode = new();

            string nsStr = "";

            if (serviceHost.ContainingNamespace is { IsGlobalNamespace: false } ns)
            {
                hostCode.Append("namespace ").Append(nsStr = ns.ToDisplayString()).AppendLine(@";");
            }

            string typeName = serviceHost.ToTypeNameFormat();

            hostCode.Append(@"
public partial class ").Append(typeName).Append(@"
{
	static volatile global::System.Net.Quic.QuicListener listener = null!;
	static volatile global::System.Threading.CancellationTokenSource cancelTokeSource = null!;
	internal static string certPath = global::System.IO.Path.Combine(global::System.AppDomain.CurrentDomain.BaseDirectory, ""localhost.pfx"");
	internal static global::System.Net.Security.SslApplicationProtocol protocol = new(""lsl"");

	private static global::System.Net.Quic.QuicServerConnectionOptions connectionOptions = CreateConnection();

	[global::System.Diagnostics.DebuggerHidden, global::System.Diagnostics.DebuggerNonUserCode, global::System.Diagnostics.DebuggerStepThrough]
	static global::System.Net.Quic.QuicServerConnectionOptions CreateConnection()
	{
		// Generate a self-signed certificate if in debug mode
#if DEBUG
		if (!global::System.IO.File.Exists(certPath))
		{
			global::System.Console.WriteLine(""Creating self-signed certificate..."");

			var psi = new global::System.Diagnostics.ProcessStartInfo
			{
				FileName = ""powershell"",
				Arguments = $@""-Command """"New-SelfSignedCertificate -DnsName 'localhost' -CertStoreLocation 'cert:\\LocalMachine\\My' | Export-PfxCertificate -FilePath '{certPath}' -Password (ConvertTo-SecureString -String 'D34lW17h' -AsPlainText -Force)"""""",
				RedirectStandardOutput = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};

			global::System.Diagnostics.Process.Start(psi)?.WaitForExit();

			global::System.Console.WriteLine(""Certificate created."");
		}
#endif

		return new global::System.Net.Quic.QuicServerConnectionOptions
		{
			IdleTimeout = global::System.TimeSpan.FromMinutes(5),
			MaxInboundBidirectionalStreams = 1000,
			MaxInboundUnidirectionalStreams = 10,
			DefaultStreamErrorCode = 0x0A,
			DefaultCloseErrorCode = 0x0B,
			ServerAuthenticationOptions = new global::System.Net.Security.SslServerAuthenticationOptions
			{
				ApplicationProtocols = [ protocol ],
				ServerCertificate = new global::System.Security.Cryptography.X509Certificates.X509Certificate2(certPath, ""D34lW17h""),
				ClientCertificateRequired = false
			}
		};
	}

	volatile static global::System.Net.Quic.QuicListenerOptions listenerOptions = new()
	{
		ListenEndPoint = new global::System.Net.IPEndPoint(new global::System.Net.IPAddress(new byte[16]), 5000),
		ApplicationProtocols = new global::System.Collections.Generic.List<global::System.Net.Security.SslApplicationProtocol> { protocol },
		ConnectionOptionsCallback = [global::System.Diagnostics.DebuggerHidden, global::System.Diagnostics.DebuggerStepThrough] static (connection, sslHello, token) =>
		{
			return new global::System.Threading.Tasks.ValueTask<global::System.Net.Quic.QuicServerConnectionOptions>(connectionOptions);
		}
	};

	[global::System.Diagnostics.DebuggerHidden, global::System.Diagnostics.DebuggerStepThrough]
	public static async global::System.Threading.Tasks.ValueTask StartAsync()
	{
		listener = await global::System.Net.Quic.QuicListener.ListenAsync(listenerOptions).ConfigureAwait(false);

		cancelTokeSource = new global::System.Threading.CancellationTokenSource();

		global::System.Console.WriteLine(""Server started..."");

        using ").Append(typeName).Append(@" ___services = new ").Append(typeName).Append(@"();

        [___MS_LOGGER_VAR_PLACEHOLDER___]

		ListenConnections(cancelTokeSource.Token);
	}

	[global::System.Diagnostics.DebuggerHidden, global::System.Diagnostics.DebuggerStepThrough, global::System.Diagnostics.DebuggerNonUserCode]
	async static void ListenConnections([___MS_LOGGER_ARG_PLACEHOLDER___]").Append(typeName).Append(@" ___services, global::System.Threading.CancellationToken token)
	{
		global::System.Console.WriteLine(""Waiting clients..."");
		try
		{
			while (!token.IsCancellationRequested)
			{
				HandleConnectionAsync(await listener.AcceptConnectionAsync(token).ConfigureAwait(false), [___MS_LOGGER_PARAM_PLACEHOLDER___]___services, token);
			}
		}
		catch (global::System.Net.Quic.QuicException e)
		{
			if (e.QuicError is not global::System.Net.Quic.QuicError.OperationAborted)
				global::System.Console.WriteLine(""Host error: "" + e);
		}
	}

	[global::System.Diagnostics.DebuggerHidden, global::System.Diagnostics.DebuggerStepThrough, global::System.Diagnostics.DebuggerNonUserCode]
	private static async void HandleConnectionAsync(global::System.Net.Quic.QuicConnection connection, [___MS_LOGGER_ARG_PLACEHOLDER___]").Append(typeName).Append(@" ___services, global::System.Threading.CancellationToken token)
	{
		global::System.Console.WriteLine(""Connected with client..."");

		try
		{
			await using (connection)
			{
				while (!token.IsCancellationRequested)
				{
					HandleStreamAsync(await connection.AcceptInboundStreamAsync(token).ConfigureAwait(false), [___MS_LOGGER_PARAM_PLACEHOLDER___]___services, token);
				}
			}
		}
		catch (global::System.Net.Quic.QuicException e)
		{
			if (e.QuicError is not global::System.Net.Quic.QuicError.ConnectionAborted)
				global::System.Console.WriteLine(""Connection error: "" + e);
		}
	}

	[global::System.Diagnostics.DebuggerHidden, global::System.Diagnostics.DebuggerStepThrough, global::System.Diagnostics.DebuggerNonUserCode]
	static async void HandleStreamAsync(global::System.Net.Quic.QuicStream stream, [___MS_LOGGER_ARG_PLACEHOLDER___]").Append(typeName).Append(@" ___services, global::System.Threading.CancellationToken token)
	{
		await using (stream)
		{
			global::System.Memory<byte> opId = new byte[4]; //reserve operation id space for reading

			await stream.ReadExactlyAsync(opId, token).ConfigureAwait(false); // fill the operation id

			await requestHandlers[global::System.BitConverter.ToInt32(opId.Span)](stream, [___MS_LOGGER_PARAM_PLACEHOLDER___]___services, token);
		}
	}

    public static async void ListenAsync()
    {
        var cancellationTokenSource = new global::System.Threading.CancellationTokenSource();

        var token = cancellationTokenSource.Token;

        using var listener = await global::KcpTransport.KcpListener.ListenAsync(acceptFromHost, token);

        using ").Append(typeName).Append(@" ___services = new ").Append(typeName).Append(@"();

        [___MS_LOGGER_VAR_PLACEHOLDER___]

        while (!token.IsCancellationRequested)
        {
            try
            {
                ProcessClientMessageAsync([___MS_LOGGER_PARAM_PLACEHOLDER___]___services, await listener.AcceptConnectionAsync(token), token);
            }
            catch (global::System.Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }

    protected static async void ProcessClientMessageAsync([___MS_LOGGER_ARG_PLACEHOLDER___]").Append(typeName).Append(@" ___services, global::KcpTransport.KcpConnection connection, global::System.Threading.CancellationToken token = default)
    {
        global::System.Memory<byte> __bytes__ = new byte[0];

        using (connection)
        using (var ___stream = await connection.OpenOutboundStreamAsync())
        try
        {
            READ_REQUEST:

			__bytes__ = new byte[44];

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
                                    resultExpression = () => hostCode.Append("global::SourceCrafter.MemLink.ResponseStatus.Success, 0"),
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

                                        foreach (var paramAttr in param.GetAttributes())
                                        {
                                            switch (paramAttr.AttributeClass?.ToGlobalNamespaced())
                                            {
                                                case "global::SourceCrafter.MemLink.ServiceAttribute":

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

                                                    goto NEXT_PARAM;
                                            }
                                        }

                                        switch (param.Type.ToGlobalNonGenericNamespace())
                                        {
                                            case "global::Microsoft.Extensions.Logging.ILogger"
                                                when isMsLoggerInstalled && param.Type is INamedTypeSymbol { IsGenericType: true, TypeArguments: [{ } genericLogger] }:

                                                invokeParams += () =>
                                                {
                                                    (UseComma(ref separateParams) ? hostCode.Append(", ") : hostCode)
                                                        .Append("___loggerFactory.CreateLogger<").Append(genericLogger.ToGlobalNamespaced()).Append(">()");
                                                };

                                                continue;

                                            case cancelTokenFullTypeName:

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

                                        NEXT_PARAM:;
                                    }
                                }

                                if (requestParamsCount > 0)
                                {
                                    if (requestTypes != null)
                                    {

                                        hostCode.Append(@"await ___stream.ReadExactlyAsync(__bytes__ = new byte[___requestlen], token);

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

                    __bytes__ = global::MemoryPack.MemoryPackSerializer.Serialize((");

                                resultExpression();

                                hostCode.Append(@"));");

                                if(!hasEmptyParams || returnsType)
                                {
                                    hostCode.Append(@"

                    global::System.BitConverter.GetBytes(__bytes__.Length - 8).CopyTo(__bytes__[4..8]);");
                                }
                                
                                hostCode.Append(@"

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
                {
                    await ___stream.WriteAsync(global::MemoryPack.MemoryPackSerializer.Serialize((global::SourceCrafter.MemLink.ResponseStatus.NotFound, 0)), cancellationToken: token);

                    goto READ_REQUEST;
                }
            }
        }
        catch (global::KcpTransport.KcpDisconnectedException)
        {
            // TODO: implement cached logger
        }
        catch (global::System.Exception e)
        {
            __bytes__ = global::MemoryPack.MemoryPackSerializer.Serialize((global::SourceCrafter.MemLink.ResponseStatus.Failed, 0, e.ToString()));

            global::System.BitConverter.GetBytes(__bytes__.Length - 8).CopyTo(__bytes__[4..8]);
            
            await ___stream.WriteAsync(__bytes__, cancellationToken: token);
        }
    }
}");

            if (isMsLoggerInstalled)
            {
                hostCode
                    .Insert(0, @"using global::Microsoft.Extensions.Logging;
")
                    .Replace("[___MS_LOGGER_VAR_PLACEHOLDER___]", @$"var ___loggerFactory = global::Microsoft.Extensions.Logging.LoggerFactory.Create(static builder =>
        {{
            builder
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
