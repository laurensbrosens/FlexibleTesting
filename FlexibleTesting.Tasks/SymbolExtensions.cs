using Microsoft.CodeAnalysis;

namespace FlexibleTesting.Tasks;

public static class SymbolExtensions
{
    extension(ISymbol symbol)
    {
        public bool IsDeclaredIn(ISymbol? typeSymbol)
        {
            return SymbolEqualityComparer.Default.Equals(symbol.ContainingType, typeSymbol);
        }

        public bool IsEqualToSymbol(ISymbol? symbol2)
        {
            return SymbolEqualityComparer.Default.Equals(symbol, symbol2);
        }
    }
}