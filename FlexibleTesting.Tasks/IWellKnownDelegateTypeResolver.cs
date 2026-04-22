using Microsoft.CodeAnalysis;
using System.Collections.Generic;

namespace FlexibleTesting.Tasks;

internal interface IWellKnownDelegateTypeResolver
{
    ITypeSymbol ResolveDelegateType(
        Compilation compilation,
        ITypeSymbol returnTypeSymbol,
        IReadOnlyList<IParameterSymbol> parameterSymbols
    );
}
