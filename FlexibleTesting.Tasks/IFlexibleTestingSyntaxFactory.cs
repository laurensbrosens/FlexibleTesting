using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using System.Collections.Generic;

namespace FlexibleTesting.Tasks;

internal interface IFlexibleTestingSyntaxFactory
{
    ParameterSyntax CreateParameter(SyntaxGenerator syntaxGenerator, IParameterSymbol parameterSymbol);

    IEnumerable<AttributeSyntax> CreateAttributes(ISymbol symbol);

    TypeSyntax CreateTypeSyntax(ITypeSymbol typeSymbol);

    ITypeSymbol ResolveMockableDelegateType(Compilation compilation, ISymbol symbol);

    AccessorListSyntax CreateAccessorList(IPropertySymbol propertySymbol);
}
