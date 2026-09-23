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

        internal enum AsyncType : byte { None, ValueTask, Task }
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

