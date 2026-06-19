using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SourceCrafter.DependencyInjection;
using SourceCrafter.DependencyInjection.Constants;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
//using static SharedMemory.CircularBuffer;

using DependencyKey = (byte lifetime, int typeHash, int keyHash);

namespace SourceCrafter.LiteSpeedLink.Helpers
{
    public static class Extensions
    {
        internal readonly static SymbolDisplayFormat
            _globalizedNamespace = new(
                memberOptions:
                    SymbolDisplayMemberOptions.IncludeType |
                    SymbolDisplayMemberOptions.IncludeModifiers |
                    SymbolDisplayMemberOptions.IncludeExplicitInterface |
                    SymbolDisplayMemberOptions.IncludeParameters |
                    SymbolDisplayMemberOptions.IncludeContainingType |
                    SymbolDisplayMemberOptions.IncludeConstantValue |
                    SymbolDisplayMemberOptions.IncludeRef,
                globalNamespaceStyle:
                    SymbolDisplayGlobalNamespaceStyle.Included,
                typeQualificationStyle:
                    SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                genericsOptions:
                    SymbolDisplayGenericsOptions.IncludeTypeParameters |
                    SymbolDisplayGenericsOptions.IncludeVariance,
                miscellaneousOptions:
                    SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                    SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier,
                parameterOptions:
                    SymbolDisplayParameterOptions.IncludeType |
                    SymbolDisplayParameterOptions.IncludeModifiers |
                    SymbolDisplayParameterOptions.IncludeName |
                    SymbolDisplayParameterOptions.IncludeDefaultValue),
            _globalizedNonGenericNamespace = new(
                globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes),
            _symbolNameOnly = new(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly),
            _typeNameFormat = new(
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance,
                miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        extension(ISymbol t)
        {
            internal string GlobalNamespaced => t.ToDisplayString(_globalizedNamespace);

            internal string GlobalNonGenericNamespace => t.ToDisplayString(_globalizedNonGenericNamespace);

            internal string NameOnly => t.ToDisplayString(_symbolNameOnly);

            public string MetadataLongName => t
                .ToDisplayParts()
                .Select(t => t.Symbol is { MetadataName: string meta }
                    ? meta.Contains('`') ? t.Symbol.Name + "Of" : meta : null)
                .Aggregate("", (x, y) => x + y);
            
        }

        const SymbolDisplayParameterOptions paramsOptions =
                SymbolDisplayParameterOptions.IncludeType |
                SymbolDisplayParameterOptions.IncludeName |
                SymbolDisplayParameterOptions.IncludeDefaultValue;

        extension(IParameterSymbol symbol)
        {
            public string GetString()
            {
                return symbol.ToDisplayString(_globalizedNamespace.WithParameterOptions(paramsOptions));
            }
        }

        internal static bool IsPrimitive(this ITypeSymbol target, bool includeObject = true) =>
            (includeObject && target.SpecialType is SpecialType.System_Object) || target.SpecialType is SpecialType.System_Enum
                or SpecialType.System_Boolean
                or SpecialType.System_Byte
                or SpecialType.System_SByte
                or SpecialType.System_Char
                or SpecialType.System_DateTime
                or SpecialType.System_Decimal
                or SpecialType.System_Double
                or SpecialType.System_Int16
                or SpecialType.System_Int32
                or SpecialType.System_Int64
                or SpecialType.System_Single
                or SpecialType.System_UInt16
                or SpecialType.System_UInt32
                or SpecialType.System_UInt64
                or SpecialType.System_String
            || target.Name is "DateTimeOffset" or "Guid"
            || (target.SpecialType is SpecialType.System_Nullable_T
                && IsPrimitive(((INamedTypeSymbol)target).TypeArguments[0]));

        extension(ITypeSymbol typeSymbol)
        {
            internal ITypeSymbol AsNonNullable() =>
            typeSymbol.Name == "Nullable"
                ? ((INamedTypeSymbol)typeSymbol).TypeArguments[0]
                : typeSymbol.WithNullableAnnotation(NullableAnnotation.None);

            internal void TryGetNullable(out ITypeSymbol outType, out bool outIsNullable)
                        => (outType, outIsNullable) = typeSymbol.SpecialType is SpecialType.System_Nullable_T
                            || typeSymbol is INamedTypeSymbol { Name: "Nullable" }
                                ? (((INamedTypeSymbol)typeSymbol).TypeArguments[0], true)
                                : typeSymbol.NullableAnnotation == NullableAnnotation.Annotated
                                    ? (typeSymbol.WithNullableAnnotation(NullableAnnotation.None), true)
                                    : (typeSymbol, false);
            internal bool IsNullable()
            => typeSymbol.SpecialType is SpecialType.System_Nullable_T
                || typeSymbol.NullableAnnotation == NullableAnnotation.Annotated
                || typeSymbol is INamedTypeSymbol { Name: "Nullable" };

            internal bool AllowsNull()
#if DEBUG
                => typeSymbol.BaseType?.GlobalNonGenericNamespace is not ("global::System.ValueType" or "global::System.ValueTuple");
#else
                => typeSymbol is { IsValueType: false, IsTupleType: false, IsReferenceType: true };
#endif

            internal string TypeNameFormat => typeSymbol.ToDisplayString(_typeNameFormat);

            internal bool TryGetAsyncType(out ITypeSymbol factoryType, out bool hasReturnType, out bool isValueTask)
            {
                hasReturnType = isValueTask = false;

                switch (factoryType = typeSymbol)
                {
                    case INamedTypeSymbol { Name: "Task" or "ValueTask", ContainingNamespace: { Name: "Tasks", ContainingNamespace: { Name: "Threading", ContainingNamespace.Name: "System" } }, TypeArguments: var typeArgs }:

                        if (typeArgs is [{ } firstTypeArg])
                        {
                            hasReturnType = true;
                            factoryType = firstTypeArg;
                        }

                        isValueTask = factoryType.Name == "ValueTask";


                        return true;

                    default:

                        return false;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static string Wordify(this string identifier, short upper = 0) => ToJoined(identifier, " ", upper);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static StringBuilder AddSpace(this StringBuilder sb, int count = 1) => sb.Append(new string(' ', count));

        static readonly Lifetime[] lifeTimes = [Lifetime.Singleton, Lifetime.Scoped, Lifetime.Transient];

        static string ToJoined(string identifier, string separator = "-", short casing = 0)
        {
            var buffer = new char[identifier.Length * (separator.Length + 1)];
            var bufferIndex = 0;

            for (int i = 0; i < identifier.Length; i++)
            {
                char ch = identifier[i];
                bool isLetterOrDigit = char.IsLetterOrDigit(ch), isUpper = char.IsUpper(ch);

                if (i > 0 && isUpper && char.IsLower(identifier[i - 1]))
                {
                    separator.CopyTo(0, buffer, bufferIndex, separator.Length);
                    bufferIndex += separator.Length;
                }
                if (isLetterOrDigit)
                {
                    buffer[bufferIndex++] = (casing, isUpper) switch
                    {
                        (1, false) => char.ToUpperInvariant(ch),
                        (-1, true) => char.ToLowerInvariant(ch),
                        _ => ch
                    };
                }
            }
            return new string(buffer, 0, bufferIndex);
        }

        internal static string? Pascalize(this string str)
        {
            if (string.IsNullOrEmpty(str))
                return str;

            ReadOnlySpan<char> span = str.AsSpan();
            Span<char> result = stackalloc char[span.Length];
            int resultIndex = 0;
            bool newWord = true;

            foreach (char c in span)
            {
                if (char.IsWhiteSpace(c) || c == '-' || c == '_')
                {
                    newWord = true;
                }
                else
                {
                    if (newWord)
                    {
                        result[resultIndex++] = char.ToUpperInvariant(c);
                        newWord = false;
                    }
                    else
                    {
                        result[resultIndex++] = c;
                    }
                }
            }

            return result[0..resultIndex].ToString();
        }

        internal delegate bool ChildDependencyHandler(bool childExists, bool isChildValid, Lifetime childLifetime, AsyncType isChildAsync, int childParamCount, bool isNullChildType, bool isUnkeyedInternalPrimitive);

        internal enum AsyncType : byte { None, ValueTask, Task }
        internal class ResolverBuilder(string toStr)
        {
            internal DependencyKey Key;
            internal AsyncType AsyncType;
            internal HashSet<(int, bool)> AsyncNestedDeps = [];
            internal string ExportTypeFullName = null!;
            internal int ParamsLength;

            public override string ToString() => toStr;
        }
        internal static Disposability GetDisposability(this ITypeSymbol type)
        {
            if (type is null) return Disposability.None;

            Disposability disposability = Disposability.None;

            foreach (var iFace in type.AllInterfaces)
            {
                switch (iFace.GlobalNonGenericNamespace)
                {
                    case "global::System.IDisposable" when disposability is Disposability.None:
                        disposability = Disposability.Disposable;
                        break;
                    case "global::System.IAsyncDisposable" when disposability < Disposability.AsyncDisposable:
                        return Disposability.AsyncDisposable;
                }
            }

            return disposability;
        }

        internal static string RemoveDuplicates(this string? input)
        {
            if ((input = input?.Trim()) is null or "")
                return "";

            var result = "";
            var wordStart = 0;

            for (int i = 1; i < input.Length; i++)
            {
                if (char.IsUpper(input[i]))
                {
                    string word = input[wordStart..i];

                    if (!result.EndsWith(word))
                    {
                        result += word;
                    }

                    wordStart = i;
                }
            }

            string lastWord = input[wordStart..];

            if (!result.EndsWith(lastWord, StringComparison.OrdinalIgnoreCase))
            {
                result += lastWord;
            }

            return result;
        }

        static readonly EqualityComparer<DependencyKey> defaultKeyComparer = EqualityComparer<DependencyKey>.Default;

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


        internal static readonly int EmptyStringHashCode = "".GetHashCode();

        internal static Dictionary<DependencyKey, ServiceMetadata> GetServices(this Compilation compilation, ITypeSymbol containerType, out AsyncType containerAsyncType)
        {
            Dictionary<DependencyKey, ResolverBuilder> dependencyAsValueBuilders = new(defaultKeyComparer);
            Dictionary<DependencyKey, ServiceMetadata> services = new(EqualityComparer<DependencyKey>.Default);
            containerAsyncType = AsyncType.None;
            Dictionary<DependencyKey, string> methodNamesMap = new(defaultKeyComparer);
            HashSet<string> methodsRegistry = [];
            // ITypeSymbol cancelTokenType = default;
            // ,

            //         var cancelTokenType = compilation.GetTypeByMetadataName(CancelTokenFQMetaName)
            Disposability
                containerDisposability = Disposability.None,
                scopedDisposability = Disposability.None;
            bool hasAsyncDependencies = false, hasScopedDependencies = false;
            var isInterfaceProvider = containerType.TypeKind == TypeKind.Interface;

            var providerTypeName = containerType.TypeNameFormat;
            var className = isInterfaceProvider ? providerTypeName[1..] : providerTypeName;

            var fullProviderImplName = containerType.ContainingNamespace.GlobalNamespaced + '.' + className;
            int
                asyncScopedDisposable = 0,
                asyncSingletonDisposable = 0,
                asyncScopedAsyncDisposable = 0,
                asyncSingletonAsyncDisposable = 0,
                scopedDisposable = 0,
                singletonDisposable = 0,
                scopedAsyncDisposable = 0,
                singletonAsyncDisposable = 0;

            foreach (var attr in containerType.GetAttributes())
            {
                TryBuildServiceCore(attr, null, out _, out var metadata);
            }

            return services;

            bool TryBuildServiceCore(
                AttributeData? attr,
                ISymbol? sourceSymbol,
                out ResolverBuilder resolver,
                out ServiceMetadata metadata,
                ChildDependencyHandler? validateAsChildDependency = null)
            {
                metadata = null!;
                var (sourceKind, sourceType) = sourceSymbol?.Kind switch
                {
                    SymbolKind.Parameter => (SymbolKind.Parameter, ((IParameterSymbol)sourceSymbol).Type),
                    SymbolKind.Property => (SymbolKind.Property, ((IPropertySymbol)sourceSymbol).Type),
                    SymbolKind.Field => (SymbolKind.Field, ((IFieldSymbol)sourceSymbol).Type),
                    SymbolKind.Method => (SymbolKind.Method, ((IMethodSymbol)sourceSymbol).ReturnType),
                    _ => (SymbolKind.Discard, null)
                };

                bool
                    isSimpleTransient = false,
                        isExternal = false,
                    isCached = false,
                    isValid = false,
                    //    hasAsyncDependencies = false,
                    isStaticFactory = false,
                    //    //isTransient = false,
                    //    useWhenAll = false,
                    needsCancelToken = false,
                    isFactory = false;

                int
                   keyHashCode = EmptyStringHashCode,
                   typeHashCode = 0;

                string
                    name = string.Empty,
                    //    whenAll = string.Empty,
                    metadataKey
                ;

                string?
                    nameOrFormat = null;

                AsyncType
                    asyncType = default,
                    initialAsyncType = default;

                DependencyKey key = default;

                ImmutableArray<IParameterSymbol>
                    defaultParamValues = [],
                    prms = [];

                Disposability
                    disposability = default;
                AttributeSyntax
                   attrSyntax = null!;
                INamedTypeSymbol?
                   attrClass;
                Lifetime
                    lifetime = default;
                ITypeSymbol?
                    interfaceType = null;
                ISymbol?
                   factory = null;
                SymbolKind
                   factoryKind = default;
                ITypeSymbol
                    exportType = null!,
                    type = null!;

                resolver = null!;
                //#endif
                var (backingFieldName, resolverMemberName) = ("", "");

                ref var existingOrNewValueBuilder = ref Unsafe.AsRef(in resolver);

                if (!IsValidServiceAttribute(attr))
                {
                    return false;
                }

                var typeFullName = type.GlobalNamespaced;
                var exportTypeFullName = exportType.GlobalNamespaced;

                //(lifetime, interfaceType?.ToDisplayString(), type.ToDisplayString(), name).Dump("Checking:");

                var deepParamsCount = 0;

                var exists = dependencyAsValueBuilders.TryGetValue(key, out resolver);

                if (exists) existingOrNewValueBuilder = resolver; ;


                if (validateAsChildDependency?.Invoke(
                    exists,
                    isValid,
                    lifetime,
                    existingOrNewValueBuilder?.AsyncType ?? asyncType,
                    existingOrNewValueBuilder?.ParamsLength ?? prms.Length,
                    type is null,
                    isExternal && name is "") is false)
                {
                    return false;
                }

                if (exists)
                {
                    ResolveMemberNames(out backingFieldName, out resolverMemberName);
                    return false;
                }
                var builderMetadataName = $"{lifetime} {exportTypeFullName + (exportTypeFullName == typeFullName ? null : $"<{typeFullName}>")} {name}".Trim();
                existingOrNewValueBuilder = new(builderMetadataName)
                {
                    Key = key,
                    ExportTypeFullName = exportTypeFullName,
                    AsyncType = asyncType,
                    ParamsLength = prms.Length
                };

                disposability = type!.GetDisposability();

                if (isCached)
                {
                    if (lifetime is Lifetime.Scoped && disposability > scopedDisposability)
                        scopedDisposability = disposability;
                    else if (disposability > containerDisposability)
                        containerDisposability = disposability;
                }

                metadataKey = $"[{lifetime}|{exportTypeFullName}|{name}]";

                exists = services.TryGetValue(key, out metadata);

                if (isSimpleTransient)
                {
                    if (!exists) services[key] = CreateServiceMetadata();

                    return exists;
                }

                var paramsToResolve = prms.Length;

                byte paramPos = 0;

                foreach (var prm in prms)
                {
                    var paramIndex = paramPos++;
                    var paramAsyncType = prm.Type.TryGetAsyncType(out var paramType, out var hasReturnType, out var isValueTask);
                    var paramTypeHashCode = paramType.GlobalNamespaced.GetHashCode();

                    var isPrimitiveParamType = paramType.IsPrimitive();

                    ResolverBuilder foundService = null!;
                    var foundAsyncType = AsyncType.None;
                    HashSet<(int, bool)> existingAsyncDepsCalls = [];
                    DependencyKey resolvedKey = default;

                    if (prm.GetAttributes() is { Length: > 0 } paramAttrs)
                    {
                        foreach (var paramAttr in paramAttrs)
                        {
                            if (TryBuildServiceCore(paramAttr, prm, out foundService, out metadata, ValidateChild))
                            {
                                break;
                            }
                        }
                    }

                    var keyHash = prm.Name.GetHashCode();
                    resolvedKey = foundService?.Key ?? default;

                    if (foundService is not null || Enumerable.Any(lifeTimes, lifeTime =>
                        dependencyAsValueBuilders.TryGetValue(resolvedKey = ((byte)lifeTime, paramTypeHashCode, keyHash), out foundService!)
                        || dependencyAsValueBuilders.TryGetValue(resolvedKey = ((byte)lifeTime, paramTypeHashCode, EmptyStringHashCode), out foundService!)))
                    {
                        var foundExportTypeFullName = foundService!.ExportTypeFullName;

                        deepParamsCount += foundService.ParamsLength;
                        foundAsyncType = foundService.AsyncType;

                        if (foundAsyncType is not 0)
                        {
                            if (asyncType is 0)
                                asyncType = AsyncType.Task;

                            if (!hasAsyncDependencies)
                                hasAsyncDependencies = true;

                        }

                        continue;
                    }

                    if (paramType.TypeKind is not TypeKind.Interface)
                    {
                        TryBuildServiceCore(null, prm, out _, out metadata, ValidateChild);
                    }
                    // else
                    // {
                    //     appendParams.Add(new(resolvedKey, AppendDefault));
                    // }

                    bool ValidateChild(bool childExists, bool isChildValid, Lifetime childLifetime, AsyncType childAsyncType, int childParamCount, bool isNullChildType, bool isUnkeyedInternalPrimitive)
                    {
                        if (!hasAsyncDependencies && childAsyncType > 0) hasAsyncDependencies = true;

                        if (childAsyncType > asyncType)
                        {
                            asyncType = childAsyncType;
                        }

                        if (childExists)
                        {
                            childParamCount += childParamCount;
                            // appendParams.Add(new(resolvedKey, buildParam));
                            return false;
                        }
                        else if ((isNullChildType && isPrimitiveParamType) || (isUnkeyedInternalPrimitive && prm.Type.IsPrimitive()) || !isChildValid)
                        {
                            deepParamsCount += 1;
                            return false;
                        }

                        return true;
                    }
                }


                if (disposability is not 0 && isCached)
                {
                    switch (asyncType is not 0, lifetime, disposability)
                    {
                        case (true, Lifetime.Scoped, Disposability.Disposable): asyncScopedDisposable++; break;
                        case (true, Lifetime.Singleton, Disposability.Disposable): asyncSingletonDisposable++; break;
                        case (true, Lifetime.Scoped, Disposability.AsyncDisposable): asyncScopedAsyncDisposable++; break;
                        case (true, Lifetime.Singleton, Disposability.AsyncDisposable): asyncSingletonAsyncDisposable++; break;
                        case (false, Lifetime.Scoped, Disposability.Disposable): scopedDisposable++; break;
                        case (false, Lifetime.Singleton, Disposability.Disposable): singletonDisposable++; break;
                        case (false, Lifetime.Scoped, Disposability.AsyncDisposable): scopedAsyncDisposable++; break;
                        case (false, Lifetime.Singleton, Disposability.AsyncDisposable): singletonAsyncDisposable++; break;
                    }
                }

                existingOrNewValueBuilder.AsyncType = asyncType;

                //TryRegisterInterceptorMethod();
                if (!(exists = services.TryGetValue(key, out metadata)))
                    services[key] = CreateServiceMetadata();

                return exists;

                string BuildSignature()
                {
                    return $"{resolverMemberName}{(
                        (asyncType is not 0 && (hasAsyncDependencies || needsCancelToken))
                            ? $"({(needsCancelToken ? "cancellationToken" : "")})"
                            : null)}";
                }

                void ResolveMemberNames(out string fieldName, out string methodName)
                {
                    methodName = nameOrFormat is not null
                        ? string.Format(nameOrFormat, name.Pascalize()!).RemoveDuplicates()
                        : SanitizeTypeName(type ?? exportType, lifetime, name.Pascalize()!);

                    methodName = isExternal ? methodName : factory?.Name ?? methodName;

                    if (factory != null && isCached && !methodName.EndsWith("Cached") && !methodName.EndsWith("Cache"))
                        methodName += "Cached";

                    fieldName = "_" + methodName.Camelize();

                    if (!(methodName.Contains("Async") || methodName.Contains("Task")) && asyncType is not 0)
                        (methodName, fieldName) = ((!isExternal && factory is null ? "Get" : "") + methodName + "Async", fieldName + "Task");
                }

                bool IsValidServiceAttribute(AttributeData? attr)
                {
                    if (attr is not { AttributeClass: { } _attrClass, ApplicationSyntaxReference: { } attrSyntaxRef }
                        || _attrClass.GlobalNamespaced is ServiceContainerAttr
                        || attrSyntaxRef.GetSyntax() is not AttributeSyntax { } _attrSyntax
                        || !TryGetAttributeParamsDefinition(compilation.GetSemanticModel(_attrSyntax.SyntaxTree).GetSymbolInfo(_attrSyntax), out ImmutableArray<IParameterSymbol> attrParams)
                        || !TryGetLifetime(_attrSyntax, ref _attrClass, ref isExternal, out lifetime))
                    {
                        return false;
                    }

                    attrSyntax = _attrSyntax;
                    attrClass = _attrClass;

                    // if (isExternal = _attrClass.ContainingNamespace.ToDisplayString() != BaseAttributesNS)
                    //     externalAssemblies.Add(_attrClass.ContainingAssembly.MetadataName.Replace(".Metadata", ""));

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

                    foreach (var (param, arg) in GetAttrParamsMap(attrParams, _attrSyntax.ArgumentList?.Arguments ?? []))
                    {
                        var model = compilation.GetSemanticModel(param.Type.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree ?? containerType.DeclaringSyntaxReferences[0].SyntaxTree);

                        switch (param.Name)
                        {
                            case ImplParamName when !isGeneric && arg is { Expression: TypeOfExpressionSyntax { Type: { } _type } }:

                                type = (ITypeSymbol)model.GetSymbolInfo(_type).Symbol!;

                                continue;

                            case IfaceParamName when sourceSymbol is IParameterSymbol { Type.TypeKind: not TypeKind.Interface } && !isGeneric && arg is { Expression: TypeOfExpressionSyntax { Type: { } type } }:

                                interfaceType = (ITypeSymbol)model!.GetSymbolInfo(type).Symbol!;

                                continue;

                            case KeyParamName when GetStringExpressionOrValue(model, param!, arg, out var keyValue):

                                keyHashCode = (name = keyValue).GetHashCode();

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

                                        factory = fieldOrProp;
                                        factoryKind = kind;
                                        isFactory = true;
                                        isStaticFactory = isStatic;

                                        var isAsync = ((fieldOrProp as IFieldSymbol)?.Type ?? ((IPropertySymbol)fieldOrProp).Type).TryGetAsyncType(out var returnType, out var hasReturnType, out var isValueTask);
                                        initialAsyncType = asyncType = isAsync ? isValueTask ? AsyncType.ValueTask : AsyncType.Task : AsyncType.None;

                                        if (returnType.TypeKind is TypeKind.Interface || returnType.IsAbstract)
                                            interfaceType ??= returnType;
                                        else
                                            type ??= returnType;


                                        continue;

                                    case { CandidateReason: CandidateReason.MemberGroup, CandidateSymbols: [IMethodSymbol { ReturnsVoid: false, IsStatic: var isStatic } method] }:

                                        factory = method;
                                        isStaticFactory = isStatic;
                                        factoryKind = SymbolKind.Method;
                                        defaultParamValues = method.Parameters;
                                        isAsync = method.ReturnType.TryGetAsyncType(out returnType, out hasReturnType, out isValueTask);
                                        initialAsyncType = asyncType = isAsync ? isValueTask ? AsyncType.ValueTask : AsyncType.Task : AsyncType.None;
                                        isFactory = true;


                                        if (returnType.TypeKind is TypeKind.Interface || returnType.IsAbstract)
                                            interfaceType ??= returnType;
                                        else
                                            type ??= returnType;

                                        continue;
                                }

                                continue;

                            case "disposability" when param.HasExplicitDefaultValue:

                                disposability = (Disposability)(byte)param.ExplicitDefaultValue!;

                                continue;
                        }
                    }
                    exportType ??= interfaceType ?? type!;
                    typeHashCode = exportType.GlobalNamespaced.GetHashCode();

                    if (!(isValid = exportType is not null && type is not null && attrClass is not null && _attrSyntax is not null)) return false;

                    if (!hasScopedDependencies && lifetime is Lifetime.Scoped)
                    {
                        hasScopedDependencies = true;
                    }

                    // if (asyncType is not 0 && factoryKind is SymbolKind.Method && !((IMethodSymbol)factory!).Parameters.Any(p => p.Type.ToDisplayString() is CancelTokenFQMetaName))
                    // {
                    //     //factory.ToDisplayString().Dump("Cancellation token should be provided");
                    //     diagnostics.Add(ServiceContainerGeneratorDiagnostics.CancellationTokenShouldBeProvided(factory, attrSyntax));
                    // }

                    if (keyHashCode == EmptyStringHashCode && sourceSymbol?.Name is { } paramName)
                    {
                        keyHashCode = (name = paramName).GetHashCode();
                    }


                    key = ((byte)lifetime, typeHashCode, keyHashCode);

                    isCached = isValid && lifetime is not Lifetime.Transient;

                    if (factory switch
                    {
                        IMethodSymbol factoryMethod => factoryMethod.Parameters,
                        IPropertySymbol { IsIndexer: true } factoryProperty => factoryProperty.Parameters,
                        IFieldSymbol => [],
                        _ => GetParameters(type)
                    }
                        is { IsDefaultOrEmpty: false, Length: > 0 } parameters)
                    {
                        prms = parameters;
                    }
                    else if (!isSimpleTransient && !isCached)
                    {
                        isSimpleTransient = true;
                    }

                    return true;

                    static ImmutableArray<IParameterSymbol> GetParameters(ITypeSymbol? implType)
                    {
                        if (implType is not INamedTypeSymbol { Constructors: var ctor, InstanceConstructors: var insCtor } || ctor.IsDefaultOrEmpty || insCtor.IsDefaultOrEmpty) return [];

                        ImmutableArray<IParameterSymbol> parameters = [];
                        int min = int.MaxValue;

                        foreach (var item in ctor.Concat(insCtor).Distinct(SymbolEqualityComparer.Default).Cast<IMethodSymbol>())
                        {
                            if (item.Parameters.IsDefaultOrEmpty || item.Parameters.Length >= min) continue;
                            min = (parameters = item.Parameters).Length;
                        }

                        return parameters;
                    }

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
                                return (value = val.ToString()!) != "";
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

                ServiceMetadata CreateServiceMetadata(bool isCancelTokenParam = false)
                {
                    ResolveMemberNames(out backingFieldName, out resolverMemberName);
#if DEBUG_SG && DISG
               Trace.WriteLine($"[DISG] Registering (lifetime: {lifetime}, typeHash: {typeFullName}, keyHash: '{name}'): [lifetime: {(byte)lifetime}, typeHash: {typeHashCode}, keyHash: '{keyHashCode}']");
#endif
                    return new()
                    {
                        ExportType = exportType,
                        Key = name,
                        Lifetime = lifetime,
                        Disposability = disposability,
                        ContainerDisposability = containerDisposability,
                        IsCached = isCached,
                        IsExternal = false,
                        IsAsync = asyncType != AsyncType.None,
                        IsFactory = isFactory,
                        IsKeyed = name != "",
                        IsSimpleTransient = isSimpleTransient,
                        HasScopedDependencies = hasScopedDependencies,
                        ResolverMember = BuildSignature(),
                        ExportTypeName = exportTypeFullName
                    };
                }
            }

            string SanitizeTypeName(ITypeSymbol type, Lifetime lifeTime, string key)
            {
                int typeHashCode = SymbolEqualityComparer.Default.GetHashCode(type),
                    keyHashCode = key.GetHashCode();

                var sanitizedTypeName = Sanitize(type).Replace(" ", "").Capitalize();

                var depKey = ((byte)lifeTime, typeHashCode, keyHashCode);

                var exists = methodNamesMap.TryGetValue(depKey, out var idOut);

                if (exists)
                {
                    return methodNamesMap[depKey] = idOut!;
                }

                key = key.Capitalize();

                if (key is "")
                {
                    if (!methodsRegistry.Add(idOut = sanitizedTypeName)) methodsRegistry.Add(idOut = $"{lifeTime}{sanitizedTypeName}");
                }
                else if (!(methodsRegistry.Add(idOut = key)
                    || methodsRegistry.Add(idOut = $"{key}{sanitizedTypeName}")
                    || methodsRegistry.Add(idOut = $"{lifeTime}{key}")))
                {
                    methodsRegistry.Add(idOut = $"{lifeTime}{key}{sanitizedTypeName}");
                }

                return methodNamesMap[depKey] = idOut!;

                static string Sanitize(ITypeSymbol type)
                {
                    switch (type)
                    {
                        case INamedTypeSymbol { IsTupleType: true, TupleElements: { Length: > 0 } els }:

                            return "TupleOf" + string.Join("", els.Select(f => Sanitize(f.Type)));

                        case INamedTypeSymbol { IsGenericType: true, TypeParameters: { } args }:

                            return type.Name + "Of" + string.Join("", args.Select(Sanitize));

                        default:

                            string typeName = type.TypeNameFormat;

                            if (type is IArrayTypeSymbol { ElementType: { } elType })
                                typeName = Sanitize(elType) + "Array";

                            return char.ToUpperInvariant(typeName[0]) + typeName[1..].TrimEnd('?', '_');
                    }
                }
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
        internal static string Camelize(this string str)
        {
            return str is [{ } f, .. { } rest] ? char.ToLower(f) + rest : str;
        }
        internal static string Capitalize(this string str)
        {
            return str is [{ } f, .. { } rest] ? char.ToUpper(f) + rest : str;
        }
    }
}

