using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;

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
        if (methodA.Parameters.Length != methodB.Parameters.Length)
        {
            return false;
        }

        for (int i = 0; i < methodA.Parameters.Length; i++)
        {
            if (methodA.Parameters[i].Type.ToDisplayString() != methodB.Parameters[i].Type.ToDisplayString())
            {
                return false;
            }
        }

        return true;
    }
}
