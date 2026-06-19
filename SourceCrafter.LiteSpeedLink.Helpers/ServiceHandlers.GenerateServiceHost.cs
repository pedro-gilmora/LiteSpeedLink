using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SourceCrafter.DependencyInjection;
using SourceCrafter.DependencyInjection.Constants;
using SourceCrafter.LiteSpeedLink.Helpers;
//using SourceCrafter.Helpers;

using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;

public partial class ServiceHandlersGenerator
{
    private const string IServiceUnit = "global::SourceCrafter.LiteSpeedLink.IServiceUnit";

    private static void GenerateServiceHost(SourceProductionContext sourceGenCtx, Compilation compilation, int compilationId, in (INamedTypeSymbol, int) serviceHostDesc, CancellationToken cancelToken)
    {
        var identity = compilation.Assembly.Identity;
        var (serviceHost, connectionType) = serviceHostDesc;

        var model = compilation.GetSemanticModel(serviceHost.DeclaringSyntaxReferences[0].SyntaxTree);
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
                  "global::SharedMemory.RpcBuffer",
                  "global::System.IDisposable",
                  null,
                  null,
                  null),
        };

        var handlerReturnType = connectionType switch { 0 => "byte[]?", 1 => "int", _ => "global::System.IO.Pipelines.FlushResult" };

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
            ").Append(connectionType is 0 ? "memoryRpcName" : "port").Append(certParam).Append(@", 
            provider.HandleRequestsAsync, 
            ");

        var onFinalizePoint = hostCode.Length;

        if (connectionType == 0)
            hostCode.Append(@",
            ").Append(1000);

        if (certArg != null)
            hostCode.Append(@",
            ").Append(certArg);

        hostCode.Append(@", 
            cancelToken);
	}
    	
    private ");

        int? handlerResultType = null;
        bool isMemoryAsync = false;

        if (connectionType > 0)
            hostCode.Append("async global::System.Threading.Tasks.ValueTask<").Append(handlerReturnType).Append(">");
        else
        {
            handlerResultType = hostCode.Length;
        }

        hostCode.Append(@" HandleRequestsAsync(
        long id,
        ").Append(context).Append(@" __context,
        global::System.Threading.CancellationToken __token)
    {
        switch(id)
        {");

        int providerTypeId = SymbolEqualityComparer.Default.GetHashCode(serviceHost);

        string
            fullContainerTypeName = serviceHost.GlobalNamespaced,
            nsMeta = serviceHost.ContainingNamespace.MetadataLongName,
            typeMeta = serviceHost.MetadataLongName,
            justTypeMeta = nsMeta.Length > 0 ? typeMeta.Replace(nsMeta, "") : typeMeta,
            hintName = nsStr.Length > 0 ? nsStr + "." + justTypeMeta : justTypeMeta;

        //string? serviceCommaSep = default;

        Disposability? containerDisposability = default;

        var services = compilation.GetServices(serviceHost, out var containerAsyncType);

        foreach (var dependency in services.Values)
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

            if (!isMemoryAsync && (dependency.IsAsync || dependency.Disposability is Disposability.AsyncDisposable)) isMemoryAsync = true;

            containerDisposability ??= dependency.ContainerDisposability;


            foreach (var member in dependency.ExportType.GetMembers())
            {
                if (!(member is IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false } method))
                {
                    continue;
                }

                string
                    methodName = method.NameOnly,
                    globalizedMethodName = method.GlobalNamespaced;

                bool isMethodAsync = (method.ReturnType.Name.EndsWith("Task")
                        && method.ReturnType.ToString().StartsWith("System.Threading.Tasks"))
                        || dependency.Lifetime is Lifetime.Scoped;

                var (_async, _await) = isMethodAsync
                        ? ("async ", "await ")
                        : default;

                var serviceId = GetServiceId(globalizedMethodName);

                if (!isMemoryAsync && isMethodAsync) isMemoryAsync = true;



                //System.Console.WriteLine(@""Server received call to: ").Append(globalizedMethodName).Append(@"
    
                //    ServiceId: ").Append(serviceId).Append(@""");

                hostCode.Append(@"
        case ").Append(serviceId).Append(@": //Id for: [").Append(globalizedMethodName).Append(@"]
            ");

                var provider = "";

                if (dependency.Lifetime is Lifetime.Scoped)
                {
                    if (dependency.ContainerDisposability is Disposability.AsyncDisposable)
                    {
                        hostCode.Append("await using ");
                    }
                    else if (dependency.ContainerDisposability is Disposability.Disposable)
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
                        var paramType = param.Type.GlobalNamespaced;

                        foreach (var paramAttr in param.GetAttributes())
                        {
                            var pAttrCls = paramAttr.AttributeClass!;

                            if (!IsValidServiceAttribute(
                                model,
                                paramAttr,
                                param,
                                out var pExportTypeKey,
                                out var pLifetime,
                                out var nameKey,
                                out var pIsValid) || !pIsValid)

                                continue; //check next attribute

                            var pDKey = ((byte)pLifetime, pExportTypeKey, nameKey);

                            if (!services.TryGetValue(pDKey, out var paramDependency))
                                goto NEXT_PARAM;

                            if (!isMemoryAsync && paramDependency.IsAsync) isMemoryAsync = true;

                            invokeParams += () =>
                                {
                                    if (Exchange(ref separateParams)) hostCode.Append(", ");

                                    hostCode.Append(dependency.ResolverMember);

                                    hostCode.Append("(");

                                    if (paramAttr.ConstructorArguments is [{ Value: string name }])
                                    {
                                        hostCode.Append(@"""").Append(name).Append(@"""");
                                    }

                                    hostCode.Append(")");
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

                bool shouldParenthizeExpression = dependency.IsAsync || (dependency.Lifetime is Lifetime.Transient && !dependency.IsCached);

                if (dependency.IsAsync) hostCode.Append(_await);

                if (shouldParenthizeExpression) hostCode.Append('(');

                if (dependency.IsAsync) hostCode.Append(_await);

                hostCode.Append(provider).Append(dependency.ResolverMember);

                if (shouldParenthizeExpression) hostCode.Append(')');

                hostCode.Append(".").Append(methodName).Append('(');

                invokeParams?.Invoke();

                hostCode.Append(@");

            return ").Append(connectionType == 0 ? "__context.Return(" : "await __context.ReturnAsync(");

                if (outCount > 0)
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

                if (connectionType > 0) hostCode.Append(", __token");

                hostCode.Append(@");
");
            }
        }

        hostCode.Append(@"

        default:

            return ").Append(connectionType == 0 ? "null" : "await __context.ReturnAsync(false)").Append(@";
        }
    }
}");
        if (handlerResultType.HasValue)
        {
            hostCode.Insert(handlerResultType.Value, /*isMemoryAsync ? "global::System.Threading.Tasks.Task<byte[]?>" : */"byte[]?");
        }

        hostCode.Insert(onFinalizePoint, containerDisposability switch
        {
            Disposability.AsyncDisposable => "() => provider.DisposeAsync().GetAwaiter().GetResult()",
            Disposability.Disposable => "() => provider.Dispose()",
            _ => "() => {}"
        });

        sourceGenCtx.AddSource(hintName + ".host.cs", hostCode.ToString());
    }

    public static long GetServiceId(string input)
    {
        using MD5 md5 = MD5.Create();
        Guid id = new(md5.ComputeHash(Encoding.Default.GetBytes(input)));

        return BitConverter.ToInt64(id.ToByteArray(), 8);
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

    private static readonly int EmptyStringHashCode = "".GetHashCode();

    static bool IsValidServiceAttribute(
        SemanticModel model,
        AttributeData? attr,
        IParameterSymbol? sourceSymbol,
        out int exportTypeKey,
        out Lifetime lifetime,
        //out AttributeSyntax attrSyntax,
        out int nameKey,
        //out int keyHashCode
        //out string nameOrFormat,
        //out ISymbol factory,
        //out SymbolKind factoryKind,
        //out bool isFactory,
        //out bool isStaticFactory,
        //out object asyncType,
        //out object initialAsyncType,
        //out Disposability disposability,
        //out ITypeSymbol exportType,
        //out bool hasScopedDependencies,
        //out bool isCached,
        //out ImmutableArray<IParameterSymbol> prms,
        //out (Lifetime lifeTime, int typeHashCode, int keyHashCode) key,
        out bool isValid//,
                        //out object typeHashCode,
                        //out ImmutableArray<IParameterSymbol> defaultParamValues,
                        //out INamedTypeSymbol attrClass,
                        //out bool isExternal
        )
    {
        exportTypeKey = 0;
        ITypeSymbol type = null!, interfaceType = null!, exportType = null!;
        isValid = false;
        var name = "";
        nameKey = EmptyStringHashCode;
        lifetime = default;
        //isCached = false;
        //key = default;
        //typeHashCode = null!;
        interfaceType = type = null!;
        //isExternal = false;
        //name = null!;
        //nameOrFormat = null!;
        //factoryKind = default;
        //isFactory = false;
        //isStaticFactory = false;
        //asyncType = null!;
        //initialAsyncType = null!;
        //disposability = default;
        //prms = default;
        //defaultParamValues = default;

        bool isExternal = false;
        if (attr is not { AttributeClass: { } _attrClass, ApplicationSyntaxReference: { } attrSyntaxRef }
            || _attrClass.GlobalNamespaced is ServiceContainerAttr
            || attrSyntaxRef.GetSyntax() is not AttributeSyntax { } _attrSyntax
            || !TryGetAttributeParamsDefinition(model.GetSymbolInfo(_attrSyntax), out ImmutableArray<IParameterSymbol> attrParams)
            || !TryGetLifetime(_attrSyntax, ref _attrClass, ref isExternal, out lifetime))
        {
            type = null!;
            interfaceType = null!;
            //lifetime = default;
            //attrSyntax = null!;
            //name = null!;
            //keyHashCode = 0;
            //nameOrFormat = null!;
            //sourceSymbol = null!;
            //factory = null!;
            //factoryKind = default;
            //isFactory = false;
            //isStaticFactory = false;
            //asyncType = null!;
            //initialAsyncType = null!;
            //disposability = default;
            //exportType = null!;
            //isValid = false;
            //hasScopedDependencies = false;
            //isCached = false;
            //prms = default;
            //typeHashCode = null!;
            //defaultParamValues = default;
            return false;
        }

        //attrSyntax = _attrSyntax;
        //attrClass = _attrClass;
        //exportType = default!;
        //hasScopedDependencies = default;
        //keyHashCode = default;
        //factory = default!;

        if (attr.AttributeClass!.TypeArguments.Length > 0 is { } isGeneric)
        {
            switch (_attrClass!.TypeArguments)
            {
                case [{ } t1, { } t2, ..]:

                    interfaceType = t1;
                    type = t2;

                    break;

                case [{ } t1]:

                    type = t1;

                    break;
            }
        }
        nameKey = EmptyStringHashCode;
        string nameOrFormat;

        foreach (var (param, arg) in GetAttrParamsMap(attrParams, _attrSyntax.ArgumentList?.Arguments ?? []))
        {
            switch (param.Name)
            {
                case ImplParamName when !isGeneric && arg is { Expression: TypeOfExpressionSyntax { Type: { } _type } }:

                    type = (ITypeSymbol)model!.GetSymbolInfo(_type).Symbol!;

                    continue;

                case IfaceParamName when sourceSymbol is IParameterSymbol { Type.TypeKind: not TypeKind.Interface } && !isGeneric && arg is { Expression: TypeOfExpressionSyntax { Type: { } _type } }:

                    interfaceType = (ITypeSymbol)model!.GetSymbolInfo(_type).Symbol!;

                    continue;

                case KeyParamName when GetStringExpressionOrValue(model, param!, arg, out var keyValue):

                    nameKey = (name = keyValue).GetHashCode();

                    continue;

                case NameFormatParamName when GetStringExpressionOrValue(model, param, arg, out var keyValue):

                    nameOrFormat = keyValue;

                    continue;

                case SourceParamName

                    when arg?.Expression is InvocationExpressionSyntax
                    {
                        Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" },
                        ArgumentList.Arguments: [{ } methodRef]
                    }:

                    switch (model.GetSymbolInfo(methodRef.Expression))
                    {
                        case { Symbol: (IFieldSymbol or IPropertySymbol) and { Kind: var kind, IsStatic: var isStatic } fieldOrProp }:

                            //factory = fieldOrProp;
                            //factoryKind = kind;
                            //isFactory = true;
                            //isStaticFactory = isStatic;
                            //initialAsyncType = 
                            //asyncType =
                            ((fieldOrProp as IFieldSymbol)?.Type ?? ((IPropertySymbol)fieldOrProp).Type).TryGetAsyncType(out var returnType, out _, out _);

                            if (returnType.TypeKind is TypeKind.Interface || returnType.IsAbstract)
                                interfaceType ??= returnType;
                            else
                                type ??= returnType;


                            continue;

                        case { CandidateReason: CandidateReason.MemberGroup, CandidateSymbols: [IMethodSymbol { ReturnsVoid: false, IsStatic: var isStatic } method] }:

                            //factory = method;
                            //isStaticFactory = isStatic;
                            //factoryKind = SymbolKind.Method;
                            //defaultParamValues = method.Parameters;
                            //initialAsyncType = asyncType =
                            method.ReturnType.TryGetAsyncType(out returnType, out _, out _);
                            //isFactory = true;

                            if (returnType.TypeKind is TypeKind.Interface || returnType.IsAbstract)
                                interfaceType ??= returnType;
                            else
                                type ??= returnType;

                            continue;
                    }

                    continue;

                    //case "disposability" when param.HasExplicitDefaultValue:

                    //    disposability = (Disposability)(byte)param.ExplicitDefaultValue!;

                    //continue;
            }
        }

        exportType = interfaceType ?? type!;

        if (!(isValid = exportType is not null && type is not null && _attrClass is not null && _attrSyntax is not null))
        {
            //key = default;
            name = null!;
            return false;
        }

        //if (!hasScopedDependencies && lifetime is Lifetime.Scoped)
        //{
        //    hasScopedDependencies = true;
        //}

        if (nameKey == EmptyStringHashCode && sourceSymbol?.Name is { } paramName)
        {
            nameKey = (name = paramName).GetHashCode();
        }

        var typeHashCode = (interfaceType ?? type!).GlobalNamespaced.GetHashCode();

        //key = (lifeTime, typeHashCode, keyHashCode);
        var isCached = isValid && lifetime is not Lifetime.Transient;

        //if (factory switch
        //{
        //    IMethodSymbol factoryMethod => factoryMethod.Parameters,
        //    IPropertySymbol { IsIndexer: true } factoryProperty => factoryProperty.Parameters,
        //    IFieldSymbol => [],
        //    _ => GetParameters(type)
        //}
        //    is { IsDefaultOrEmpty: false, Length: > 0 } parameters)
        //{
        //    prms = parameters;
        //}

        return true;

        //static ImmutableArray<IParameterSymbol> GetParameters(ITypeSymbol? implType)
        //{
        //    if (implType is not INamedTypeSymbol { Constructors: var ctor, InstanceConstructors: var insCtor } || ctor.IsDefaultOrEmpty || insCtor.IsDefaultOrEmpty) return [];

        //    ImmutableArray<IParameterSymbol> parameters = [];
        //    int min = int.MaxValue;

        //    foreach (var item in ctor.Concat(insCtor).Distinct(SymbolEqualityComparer.Default).Cast<IMethodSymbol>())
        //    {
        //        if (item.Parameters.IsDefaultOrEmpty || item.Parameters.Length >= min) continue;
        //        min = (parameters = item.Parameters).Length;
        //    }

        //    return parameters;
        //}

        static bool TryGetAttributeParamsDefinition(SymbolInfo info, out ImmutableArray<IParameterSymbol> prms)
        {
            if (info.Symbol is IMethodSymbol { Parameters: { } _prms })
            {
                prms = _prms;
                return true;
            }
            foreach (var item in info.CandidateSymbols)
            {
                if (item is IMethodSymbol { Parameters: { } _prms2 })
                {
                    prms = _prms2;
                    return true;
                }
            }
            prms = [];
            return false;
        }

        static Span<(IParameterSymbol, AttributeArgumentSyntax?)> GetAttrParamsMap(
           ImmutableArray<IParameterSymbol> paramSymbols,
           SeparatedSyntaxList<AttributeArgumentSyntax> argsSyntax)
        {
            int i = -1;
            Span<(IParameterSymbol, AttributeArgumentSyntax?)> result = new (IParameterSymbol, AttributeArgumentSyntax?)[paramSymbols.Length];

            foreach (var param in paramSymbols)
            {
                result[++i] = argsSyntax.Count > i && argsSyntax[i] is { NameColon: null, NameEquals: null } argSyntax
                    ? (param, argSyntax)
                    : (param, argsSyntax.FirstOrDefault(arg => param.Name == arg.NameColon?.Name.Identifier.ValueText));
            }

            return result;
        }

        static bool GetStringExpressionOrValue(SemanticModel model, IParameterSymbol paramSymbol, AttributeArgumentSyntax? arg, out string value)
        {
            value = null!;

            if (arg is not null)
            {
                if (model.GetSymbolInfo(arg.Expression).Symbol is IFieldSymbol
                    {
                        IsConst: true,
                        Type.SpecialType: SpecialType.System_String,
                        ConstantValue: { } val
                    })
                {
                    return (value = val.ToString()) != "";
                }
                else if (arg.Expression is LiteralExpressionSyntax { Token.ValueText: { } valueText } e
                    && e.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    return (value = valueText) != "";
                }
            }
            else if (paramSymbol.HasExplicitDefaultValue)
            {
                value = paramSymbol.ExplicitDefaultValue?.ToString()!;
                return value != "";
            }

            return false;
        }
    }

    static bool TryGetLifetime(AttributeSyntax attrSyntax, ref INamedTypeSymbol attrClass, ref bool isExternal, out Lifetime lifetime)
    {
        if (GetLifetimeFromSyntax(attrSyntax, out lifetime)) return true;

        bool found;
        do
        {
            (isExternal, (found, lifetime)) = attrClass.GlobalNonGenericNamespace switch
            {
                SingletonAttr => (isExternal, (true, Lifetime.Singleton)),
                ScopedAttr => (isExternal, (true, Lifetime.Scoped)),
                TransientAttr => (isExternal, (true, Lifetime.Transient)),
                { } val => (val is not DependencyAttr, GetFromCtorSymbol(attrClass))
            };

            if (found) return true;

            isExternal = true;
        }
        while ((attrClass = attrClass?.BaseType!) is not null);

        return false;

        static (bool, Lifetime) GetFromCtorSymbol(INamedTypeSymbol attrClass)
        {
            foreach (var ctor in attrClass.Constructors)
                foreach (var param in ctor.Parameters)
                    if (param.Name.ToLower() is "lifetime" && param.HasExplicitDefaultValue)
                        return (true, (Lifetime)(byte)param.ExplicitDefaultValue!);

            return (false, default);
        }

        static bool GetLifetimeFromSyntax(AttributeSyntax attribute, out Lifetime lifetime)
        {
            foreach (var arg in attribute.ArgumentList?.Arguments ?? [])
            {
                if (arg is { NameColon.Name.Identifier.ValueText: "lifetime", Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: { } memberName } }
                    && Enum.TryParse(memberName, out lifetime))
                {
                    return true;
                }
            }

            lifetime = default;
            return false;
        }
    }
}
