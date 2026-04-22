using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FlexibleTesting.Tasks;

internal sealed class WellKnownDelegateTypeResolver : IWellKnownDelegateTypeResolver
{
    private const string ActionMetadataName = "System.Action";
    private const string FuncMetadataName = "System.Func";

    public ITypeSymbol ResolveDelegateType(
        Compilation compilation,
        ITypeSymbol returnTypeSymbol,
        IReadOnlyList<IParameterSymbol> parameterSymbols
    )
    {
        var delegateTypeArguments = returnTypeSymbol.SpecialType == SpecialType.System_Void
            ? parameterSymbols.Select(parameterSymbol => parameterSymbol.Type).ToArray()
            : parameterSymbols.Select(parameterSymbol => parameterSymbol.Type).Append(returnTypeSymbol).ToArray();

        var delegateMetadataName = returnTypeSymbol.SpecialType == SpecialType.System_Void
            ? (delegateTypeArguments.Length == 0 ? ActionMetadataName : $"{ActionMetadataName}`{delegateTypeArguments.Length}")
            : $"{FuncMetadataName}`{delegateTypeArguments.Length}";

        var delegateTypeDefinition =
            compilation.GetTypeByMetadataName(delegateMetadataName)
            ?? throw new InvalidOperationException($"Could not find {delegateMetadataName}");

        return delegateTypeArguments.Length == 0 ? delegateTypeDefinition : delegateTypeDefinition.Construct(delegateTypeArguments);
    }
}
