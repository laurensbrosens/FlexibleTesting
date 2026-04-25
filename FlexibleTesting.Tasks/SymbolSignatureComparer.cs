using Microsoft.CodeAnalysis;
using System.Collections.Generic;

namespace FlexibleTesting.Tasks;

public class SymbolSignatureComparer : IEqualityComparer<ISymbol>
{
    public static SymbolSignatureComparer Default { get; } = new();

    public bool Equals(ISymbol? symbolA, ISymbol? symbolB)
    {
        if (ReferenceEquals(symbolA, symbolB))
        {
            return true;
        }

        if (symbolA == null || symbolB == null)
        {
            return false;
        }

        var a = symbolA.OriginalDefinition;
        var b = symbolB.OriginalDefinition;

        if (a.Kind != b.Kind)
        {
            return false;
        }

        if (a.Name != b.Name)
        {
            return false;
        }

        if (!ContainingTypesMatch(a, b))
        {
            return false;
        }

        if (!NamespacesMatch(a, b))
        {
            return false;
        }

        if (a is IMethodSymbol methodA && b is IMethodSymbol methodB)
        {
            return MethodsMatch(methodA, methodB);
        }

        if (a is IPropertySymbol propA && b is IPropertySymbol propB)
        {
            return TypesMatch(propA.Type, propB.Type);
        }

        if (a is IFieldSymbol fieldA && b is IFieldSymbol fieldB)
        {
            return TypesMatch(fieldA.Type, fieldB.Type);
        }

        return true;
    }

    public int GetHashCode(ISymbol symbol)
    {
        var s = symbol.OriginalDefinition;
        var hash = s.Name.GetHashCode() ^ s.Kind.GetHashCode();
        if (s.ContainingType != null)
        {
            hash ^= s.ContainingType.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).GetHashCode();
        }
        else if (s.ContainingNamespace != null)
        {
            hash ^= s.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).GetHashCode();
        }
        return hash;
    }

    private static bool ContainingTypesMatch(ISymbol a, ISymbol b)
    {
        if (a.ContainingType == null && b.ContainingType == null)
        {
            return true;
        }

        if (a.ContainingType == null || b.ContainingType == null)
        {
            return false;
        }

        return a.ContainingType.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            == b.ContainingType.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static bool NamespacesMatch(ISymbol a, ISymbol b)
    {
        if (a.ContainingNamespace == null && b.ContainingNamespace == null)
        {
            return true;
        }

        if (a.ContainingNamespace == null || b.ContainingNamespace == null)
        {
            return false;
        }

        return a.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            == b.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static bool TypesMatch(ITypeSymbol? a, ITypeSymbol? b)
    {
        if (a == null && b == null)
        {
            return true;
        }

        if (a == null || b == null)
        {
            return false;
        }

        return a.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            == b.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static bool MethodsMatch(IMethodSymbol methodA, IMethodSymbol methodB)
    {
        // methodA and methodB are already OriginalDefinitions from Equals
        if (methodA.Name != methodB.Name)
        {
            return false;
        }

        if (methodA.TypeParameters.Length != methodB.TypeParameters.Length)
        {
            return false;
        }

        if (methodA.Parameters.Length != methodB.Parameters.Length)
        {
            return false;
        }

        if (!TypesMatch(methodA.ReturnType, methodB.ReturnType))
        {
            return false;
        }

        for (int i = 0; i < methodA.Parameters.Length; i++)
        {
            // Compare parameter types using OriginalDefinition to match type parameters correctly
            if (
                methodA.Parameters[i].Type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                != methodB.Parameters[i].Type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            )
            {
                return false;
            }
        }

        return true;
    }
}
