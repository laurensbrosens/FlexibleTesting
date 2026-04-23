using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace FlexibleTesting.Tasks;

internal interface IBaseInheritanceGenerator
{
    bool TryAddBaseInheritanceMembers(
        SyntaxEditor syntaxEditor,
        ClassDeclarationSyntax generatedClassNode,
        FlexibleTestingInstructions instructions,
        SemanticModel semanticModel,
        SyntaxGenerator syntaxGenerator
    );
}
