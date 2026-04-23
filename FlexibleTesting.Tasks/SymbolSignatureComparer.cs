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

        if (symbolA.Name != symbolB.Name)
        {
            return false;
        }

        if (symbolA.Kind != symbolB.Kind)
        {
            return false;
        }

        if (symbolA is IMethodSymbol methodA && symbolB is IMethodSymbol methodB)
        {
            return MethodsMatch(methodA, methodB);
        }

        return true; // For other kinds, name match is enough for our purposes (fields/properties)
    }

    public int GetHashCode(ISymbol symbol)
    {
        return symbol.Name.GetHashCode() ^ symbol.Kind.GetHashCode();
    }

    private static bool MethodsMatch(IMethodSymbol methodA, IMethodSymbol methodB)
    {
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

        var methodAOrig = methodA.OriginalDefinition;
        var methodBOrig = methodB.OriginalDefinition;

        for (int i = 0; i < methodAOrig.Parameters.Length; i++)
        {
            // Compare parameter types using ToDisplayString on the original definition to match type parameters correctly
            if (methodAOrig.Parameters[i].Type.ToDisplayString() != methodBOrig.Parameters[i].Type.ToDisplayString())
            {
                return false;
            }
        }

        return true;
    }
}
