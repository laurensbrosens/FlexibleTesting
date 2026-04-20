using Microsoft.CodeAnalysis;
using System.Collections.Generic;

namespace FlexibleTesting.Tasks;

public record FlexibleTestingInstructions(
    INamedTypeSymbol TargetType,
    string OldClassName,
    string NewClassName,
    IReadOnlyList<IMethodSymbol> MethodsToMakePublic,
    IReadOnlyList<MockableSpec> Mockables,
    string DependenciesInterfaceName,
    string DependenciesFieldName,
    string DependenciesParameterName,
    bool MockInheritance
);
