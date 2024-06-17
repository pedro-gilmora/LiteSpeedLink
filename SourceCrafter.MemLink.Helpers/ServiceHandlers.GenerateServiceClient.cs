using Microsoft.CodeAnalysis;

using SourceCrafter.Helpers;

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace SourceCrafter.MemLink.Helpers
{
    public partial class ServiceHandlersGenerator
    {
        const string cancelTokenFullTypeName = "global::System.Threading.CancellationToken";

        const SymbolDisplayParameterOptions paramsOptions =
            SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeName |
            SymbolDisplayParameterOptions.IncludeDefaultValue;
        private static void GenerateServiceClient(SourceProductionContext context, Compilation compilation, INamedTypeSymbol serviceClient, System.Security.Cryptography.MD5 mD5)
        {
            StringBuilder clientCode = new();

            string nsStr = "";

            if (serviceClient.ContainingNamespace is { IsGlobalNamespace: false } ns)
            {
                clientCode.Append("namespace ").Append(nsStr = ns.ToDisplayString()).AppendLine(@";");
            }

            //[global::Jab.ServiceProvider]
            clientCode.Append(@"
public partial class ").Append(serviceClient.ToTypeNameFormat()).Append(@"(string hostname, int port)
{");

            string
                typeName = serviceClient.ToGlobalNamespaced(),
                nsMeta = serviceClient.ContainingNamespace.ToMetadataLongName(),
                typeMeta = serviceClient.ToMetadataLongName(),
                justTypeMeta = nsMeta.Length > 0 ? typeMeta.Replace(nsMeta, "") : typeMeta,
                hintName = nsStr.Length > 0 ? nsStr + "." + justTypeMeta : justTypeMeta;

            foreach (var iFace in
                serviceClient.AllInterfaces.Where(i =>
                     i.Interfaces.Any(ii => ii.ToGlobalNamespaced() == "global::SourceCrafter.MemLink.IServiceUnit")).ToImmutableArray().AsSpan())
            {
                var fullTypeName = iFace.ToGlobalNamespaced();

                var model = compilation.GetSemanticModel(iFace.DeclaringSyntaxReferences[0].SyntaxTree);

                foreach (var member in iFace.GetMembers())
                {
                    if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false } method)
                    {
                        bool
                            hasEmptyParams = method.Parameters.IsDefaultOrEmpty,
                            hasReturnType = !method.ReturnsVoid,
                            hasResultOrParams = hasReturnType || !hasEmptyParams,
                            nameEndsWithTask = method.ReturnType.Name is [.., 'T', 'a', 's', 'k'],
                            isTask = method.IsAsync || nameEndsWithTask,
                            needsCancelToken = true;

                        var returnType = nameEndsWithTask && method.ReturnType is INamedTypeSymbol { TypeArguments: [{ } type] }
                            ? type
                            : method.ReturnType;

                        string
                            methodName = method.ToNameOnly(),
                            globalizedMethodName = method.ToMinimalDisplayString(model, 0),
                            returnFullTypeName = returnType.ToGlobalNamespaced(),
                            cancelTokenParam = null!;

                        var serviceId = GetServiceId(mD5, globalizedMethodName);


                        Action?
                            responseTypes = hasReturnType ? () => clientCode.Append(returnFullTypeName) : null, 
                            responseDeconstruct = null,
                            invokeParams = null,
                            assignOutValues = null;

                        Action<bool>? methodParams = null;

                        var outCount = responseTypes != null ? 1 : 0; 

                        //string methodSignature = method.ToMinimalDisplayString(model, 0).Replace(iFace.ToMinimalDisplayString(model, 0) + ".", "");

                        if (!hasEmptyParams)
                        {
                            var paramsComma = false;

                            foreach (var param in method.Parameters)
                            {
                                //(UseComma(ref separateParams) ? clientCode.Append(", ") : clientCode)
                                //    .Append(param.ToMinimalDisplayString(model, 0));


                                methodParams += isImplMethod =>
                                {
                                    if (UseComma(ref paramsComma)) clientCode.Append(", ");

                                    if (isImplMethod && needsCancelToken && param.IsParams && cancelTokenParam == null)
                                    {
                                        needsCancelToken = false;

                                        cancelTokenParam = "token";

                                        clientCode.Append(cancelTokenFullTypeName).Append(" token = default, ");
                                    }

                                    var paramOpts = paramsOptions;

                                    if(isImplMethod)
                                        paramOpts |= SymbolDisplayParameterOptions.IncludeModifiers;

                                    clientCode.Append(param.ToMinimalDisplayString(model, 0, new(parameterOptions: paramOpts)));

                                };

                                var paramType = param.Type.ToGlobalNamespaced();

                                if(cancelTokenParam == null && paramType == cancelTokenFullTypeName)
                                {
                                    cancelTokenParam = param.ToNameOnly();

                                    needsCancelToken = false;
                                    
                                    continue;
                                }

                                //Register params usage and in/out/ref tuple types
                                switch (param.RefKind)
                                {
                                    case RefKind.In:

                                        invokeParams += () =>
                                           clientCode.Append(", ").Append("in ").Append(param.Name);

                                        continue;

                                    case RefKind.Ref or RefKind.RefReadOnly or RefKind.RefReadOnlyParameter:
                                        outCount++;

                                        invokeParams += () =>
                                            clientCode.Append(", ").Append("ref ").Append(param.Name);

                                        //Register params to deconstruct request from deserialization
                                        responseDeconstruct += () =>
                                            clientCode.Append(", @__").Append(param.Name);

                                        //Register types for request deserialization
                                        responseTypes += () =>
                                            clientCode.Append(", ").Append(paramType);

                                        assignOutValues += () => clientCode.Append(", @__").Append(param.Name).Append(" = ").Append(param.Name);

                                        continue;
                                    case RefKind.Out:

                                        outCount++;

                                        invokeParams += () =>
                                            clientCode.Append(", ").Append("out ").Append(param.Name);

                                        //Register params to deconstruct request from deserialization
                                        responseDeconstruct += () =>
                                            clientCode.Append(", @__").Append(param.Name);

                                        //Register types for request deserialization
                                        responseTypes += () =>
                                            clientCode.Append(", ").Append(paramType);

                                        assignOutValues += () => clientCode.Append(", @__").Append(param.Name).Append(" = ").Append(param.Name);

                                        continue;
                                    default:

                                        invokeParams += () =>
                                            clientCode.Append(", ").Append(param.Name);

                                        continue;
                                };
                            }
                        }

                        //                    if (cancelTokenParam is null)
                        //                    {
                        //                        clientCode.Append(@"
                        //{

                        //    throw new global::System.NotImplementedException(""[").Append(globalizedMethodName).Append(@"] not suitable for implementation. The method requires a cancelation token to be passed as parameters"");

                        //}");
                        //                        continue;
                        //                    }

                        clientCode.Append(@"

    public async global::System.Threading.ValueTask");

                        if (outCount > 0)
                        {
                            clientCode.Append("<");

                            if(outCount > 1)
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

                        clientCode.AddSpace().Append(methodName).Append("(");

                        methodParams?.Invoke(true);

                        clientCode.AddSpace().Append(methodName).Append(@")
    {
        var @__stream = await AdquireConnectionStream(""").Append(serviceId).Append(@""");
        try
        {");

                        if (hasResultOrParams)
                        {
                            clientCode.Append(@"            
            var @__requestBytes = global::MemoryPack.MemoryPackSerializer.Serialize((""").Append(serviceId).Append(@"""");

                            invokeParams?.Invoke();

                            clientCode.Append(@"), cancellationToken: ").Append(cancelTokenParam).Append(@");

            ");
                            if (hasReturnType)
                            {
                                clientCode.Append(returnFullTypeName).Append(@" @__response;

            ");
                            }

                            clientCode.Append("(@__responseStatus");

                            if (hasReturnType)
                            {
                                clientCode.Append(", @__response");
                            }

                            responseDeconstruct?.Invoke();

                            clientCode.Append(")");
                        }
                        else
                        {
                            clientCode.Append("@__responseStatus");
                        }

                        clientCode.Append(@" = await global::MemoryPack.MemoryPackSerializer.DeserializeAsync<");

                        if (hasResultOrParams)
                        {
                            clientCode.Append("(global::SourceCrafter.MemLink.ResponseStatus");

                            if (hasReturnType)
                            {
                                clientCode.Append(", ").Append(returnFullTypeName);
                            }

                            responseTypes?.Invoke();

                            clientCode.Append(")");
                        }
                        else
                        {
                            clientCode.Append("global::SourceCrafter.MemLink.ResponseStatus");
                        }

                        clientCode.Append(@">(@__stream, cancellationToken: ").Append(cancelTokenParam).Append(@");");

                        if (hasReturnType)
                        {
                            clientCode.Append(@"

            return @__response;");
                        }

                        clientCode.Append(@"
        }
        catch (global::MemoryPack.MemoryPackSerializationException ex)
        {
            if (ex.StackTrace?.Contains(""MemoryPack.MemoryPackSerializer.Serialize["") is true)
            {
                throw new global::System.ArgumentException(""Invalid parameters"", ex);
            }
	        else if (ex.StackTrace?.Contains(""MemoryPack.MemoryPackSerializer.Deserialize["") is true)
            {
                try
                {
                    @__stream.Position = 0;

                    var (@__exResponseStatus, @__serverExceptionMessage) = await global::MemoryPack.MemoryPackSerializer.DeserializeAsync<(global::SourceCrafter.MemLink.ResponseStatus, string)>(@__stream, cancellationToken: ").Append(cancelTokenParam).Append(@");

                    switch (@__exResponseStatus)
                    {
                        case global::SourceCrafter.MemLink.ResponseStatus.NotFound:

                            throw new global::System.NotImplementedException(@$""[").Append(globalizedMethodName).Append(@"] implementation is missing from {hostname}:{port}"");

                        case global::SourceCrafter.MemLink.ResponseStatus.Failed:

                            throw new global::System.InvalidOperationException(@$""[").Append(globalizedMethodName).Append(@"] execution failed on {hostname}:{port}:
REASON:

{@__serverExceptionMessage}"");

                        default:

                            throw;
                    }
                }
                catch
                {			
                    throw;
                }
            }
        }
        catch
        {
            throw;
        }

        return default;").Append(!returnType.IsValueType && !returnType.IsNullable() ? "!;" : ";").Append(@"
    }");
                    }
                }

            }

            clientCode.Append(@"
}");

            context.AddSource(hintName + ".client.cs", clientCode.ToString());

            static bool UseComma(ref bool useParamCommaSeparator)
            {
                return ((useParamCommaSeparator, _) = (true, useParamCommaSeparator)).Item2;
            }
        }
    }
}
