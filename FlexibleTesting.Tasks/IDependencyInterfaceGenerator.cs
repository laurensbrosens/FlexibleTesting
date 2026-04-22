using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace FlexibleTesting.Tasks;

internal interface IDependencyInterfaceGenerator
{
    SyntaxNode BuildDependenciesInterface(
        SyntaxGenerator syntaxGenerator,
        FlexibleTestingInstructions instructions,
        Compilation compilation
    );

    SyntaxNode BuildMockClassInterface(SyntaxGenerator syntaxGenerator, INamedTypeSymbol mockedTypeSymbol);
}
