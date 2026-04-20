
using FlexibleTesting.Tasks;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;

public record MockableSpec(
    MockableKind Kind,
    string ContainingTypeSimpleName,
    string ContainingTypeFullName,
    string MemberName,
    string DelegateTypeDisplay,
    string DependencyMemberName,
    IReadOnlyList<MockableParameter> Parameters,
    string? ReturnTypeDisplay,
    bool IsInstanceMember
)
{
    public static MockableSpec? TryCreate(ISymbol symbol)
    {
        var containingType = symbol.ContainingType;
        if (containingType == null)
            return null;
        var containingTypeSimple = containingType.Name;
        var containingTypeFull = containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var isInstance = !symbol.IsStatic;
        switch (symbol)
        {
            case IPropertySymbol p:
                return new MockableSpec(
                    Kind: MockableKind.Property,
                    ContainingTypeSimpleName: containingTypeSimple,
                    ContainingTypeFullName: containingTypeFull,
                    MemberName: p.Name,
                    DelegateTypeDisplay: BuildDelegateTypeDisplay(Array.Empty<ITypeSymbol>(), p.Type),
                    DependencyMemberName: p.Name,
                    Parameters: Array.Empty<MockableParameter>(),
                    ReturnTypeDisplay: p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    IsInstanceMember: isInstance
                );
            case IFieldSymbol f:
                return new MockableSpec(
                    Kind: MockableKind.Field,
                    ContainingTypeSimpleName: containingTypeSimple,
                    ContainingTypeFullName: containingTypeFull,
                    MemberName: f.Name,
                    DelegateTypeDisplay: BuildDelegateTypeDisplay(Array.Empty<ITypeSymbol>(), f.Type),
                    DependencyMemberName: f.Name,
                    Parameters: Array.Empty<MockableParameter>(),
                    ReturnTypeDisplay: f.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    IsInstanceMember: isInstance
                );
            case IMethodSymbol m:
                if (m.MethodKind != MethodKind.Ordinary)
                    return null;
                var paramTypes = m.Parameters.Select(pp => pp.Type).ToArray();
                var parameters = m
                    .Parameters.Select(p => new MockableParameter(
                        Name: string.IsNullOrWhiteSpace(p.Name) ? "param" : p.Name,
                        TypeDisplay: p.Type.ToDisplayString(
                            SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
                                SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
                            )
                        ),
                        NullableAnnotation: p.Type.NullableAnnotation,
                        HasExplicitDefaultValue: p.HasExplicitDefaultValue,
                        ExplicitDefaultValue: p.HasExplicitDefaultValue ? p.ExplicitDefaultValue : null,
                        HasCallerMemberNameAttribute: p.GetAttributes()
                            .Any(a =>
                                a.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.CallerMemberNameAttribute"
                            )
                    ))
                    .ToList();
                return new MockableSpec(
                    Kind: MockableKind.Method,
                    ContainingTypeSimpleName: containingTypeSimple,
                    ContainingTypeFullName: containingTypeFull,
                    MemberName: m.Name,
                    DelegateTypeDisplay: BuildDelegateTypeDisplay(paramTypes, m.ReturnType),
                    DependencyMemberName: m.Name,
                    Parameters: parameters,
                    ReturnTypeDisplay: m.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    IsInstanceMember: isInstance
                );
            default:
                return null;
        }
    }

    private static string BuildDelegateTypeDisplay(IReadOnlyList<ITypeSymbol> parameterTypes, ITypeSymbol returnType)
    {
        static string TypeDisplay(ITypeSymbol t) =>
            t.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
                    SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
                )
            );
        if (returnType.SpecialType == SpecialType.System_Void)
        {
            if (parameterTypes.Count == 0)
                return "global::System.Action";
            var args = string.Join(", ", parameterTypes.Select(TypeDisplay));
            return $"global::System.Action<{args}>";
        }
        else
        {
            if (parameterTypes.Count == 0)
                return $"global::System.Func<{TypeDisplay(returnType)}>";
            var args = string.Join(", ", parameterTypes.Select(TypeDisplay).Concat([TypeDisplay(returnType)]));
            return $"global::System.Func<{args}>";
        }
    }
}