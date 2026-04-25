using Microsoft.CodeAnalysis;

namespace FlexibleTesting.Tasks;

public static class SymbolExtensions
{
    extension(ISymbol? symbol)
    {
        public bool IsDeclaredIn(ISymbol? typeSymbol)
        {
            return SymbolSignatureComparer.Default.Equals(symbol?.ContainingType, typeSymbol);
        }

        public bool IsEqualToSymbol(ISymbol? symbol2)
        {
            return SymbolSignatureComparer.Default.Equals(symbol, symbol2);
        }
    }
}