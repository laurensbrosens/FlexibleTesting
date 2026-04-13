using Microsoft.CodeAnalysis;

namespace FlexibleTesting.Generators;

public static class MethodSymbolExtensions
{
    extension(IMethodSymbol symbol)
    {
        /// <summary>
        /// Gets only the method signature, without the containing type.
        /// This allows us to compare methods across different classes (e.g., original and generated).
        /// </summary>
        /// <returns>Method signature as string</returns>
        public string ToSignatureString()
        {
            if (symbol == null)
            {
                return string.Empty;
            }

            var format = new SymbolDisplayFormat(
                globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
                parameterOptions: SymbolDisplayParameterOptions.IncludeType,
                miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            );

            return symbol.ToDisplayString(format);
        }
    }
}
