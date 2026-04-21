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
    private static readonly SymbolDisplayFormat FullFormat = SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
        SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
    );

    public static MockableSpec? TryCreate(ISymbol symbol)
    {
        if (symbol.ContainingType is not { } containingType)
            return null;

        var (kind, type, parameters) = symbol switch
        {
            IPropertySymbol p => (MockableKind.Property, p.Type, Array.Empty<IParameterSymbol>()),
            IFieldSymbol f => (MockableKind.Field, f.Type, Array.Empty<IParameterSymbol>()),
            IMethodSymbol m when m.MethodKind == MethodKind.Ordinary => (MockableKind.Method, m.ReturnType, m.Parameters.ToArray()),
            _ => default,
        };

        if (type == null)
            return null;

        var mockParams = parameters.Select(MapParameter).ToList();
        var paramTypes = parameters.Select(p => p.Type).ToArray();

        return new MockableSpec(
            Kind: kind,
            ContainingTypeSimpleName: containingType.Name,
            ContainingTypeFullName: containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            MemberName: symbol.Name,
            DelegateTypeDisplay: BuildDelegateTypeDisplay(paramTypes, type),
            DependencyMemberName: symbol.Name,
            Parameters: mockParams,
            ReturnTypeDisplay: type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            IsInstanceMember: !symbol.IsStatic
        );
    }

    private static MockableParameter MapParameter(IParameterSymbol p) =>
        new(
            Name: string.IsNullOrWhiteSpace(p.Name) ? "param" : p.Name,
            TypeDisplay: p.Type.ToDisplayString(FullFormat),
            NullableAnnotation: p.Type.NullableAnnotation,
            HasExplicitDefaultValue: p.HasExplicitDefaultValue,
            ExplicitDefaultValue: p.HasExplicitDefaultValue ? p.ExplicitDefaultValue : null,
            HasCallerMemberNameAttribute: p.GetAttributes()
                .Any(a => a.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.CallerMemberNameAttribute")
        );

    private static string BuildDelegateTypeDisplay(IReadOnlyList<ITypeSymbol> paramTypes, ITypeSymbol returnType)
    {
        var isVoid = returnType.SpecialType == SpecialType.System_Void;
        var types = isVoid ? paramTypes : paramTypes.Append(returnType);
        var typeList = string.Join(", ", types.Select(t => t.ToDisplayString(FullFormat)));

        return (isVoid, paramTypes.Count) switch
        {
            (true, 0) => "global::System.Action",
            (true, _) => $"global::System.Action<{typeList}>",
            (false, _) => $"global::System.Func<{typeList}>",
        };
    }
}
