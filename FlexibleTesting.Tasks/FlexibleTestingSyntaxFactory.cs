using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FlexibleTesting.Tasks;

internal sealed class FlexibleTestingSyntaxFactory : IFlexibleTestingSyntaxFactory
{
    private readonly IWellKnownDelegateTypeResolver _delegateTypeResolver;
    private readonly Action<string> _recordNamespaceName;

    public FlexibleTestingSyntaxFactory(
        IWellKnownDelegateTypeResolver delegateTypeResolver,
        Action<string> recordNamespaceName
    )
    {
        _delegateTypeResolver = delegateTypeResolver;
        _recordNamespaceName = recordNamespaceName;
    }

    public ParameterSyntax CreateParameter(SyntaxGenerator syntaxGenerator, IParameterSymbol parameterSymbol)
    {
        var parameterDeclaration = (ParameterSyntax)syntaxGenerator.ParameterDeclaration(parameterSymbol.Name, CreateTypeSyntax(parameterSymbol.Type));
        foreach (var attributeSyntax in CreateAttributes(parameterSymbol))
            parameterDeclaration = (ParameterSyntax)syntaxGenerator.AddAttributes(parameterDeclaration, attributeSyntax);

        if (parameterSymbol.HasExplicitDefaultValue)
        {
            parameterDeclaration = parameterDeclaration.WithDefault(
                SyntaxFactory.EqualsValueClause(CreateLiteralExpression(parameterSymbol.ExplicitDefaultValue))
            );
        }

        return parameterDeclaration;
    }

    public IEnumerable<AttributeSyntax> CreateAttributes(ISymbol symbol)
    {
        foreach (var attributeData in symbol.GetAttributes())
            yield return CreateAttributeSyntax(attributeData);
    }

    public TypeSyntax CreateTypeSyntax(ITypeSymbol typeSymbol)
    {
        var syntax = typeSymbol.SpecialType switch
        {
            SpecialType.System_String => SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
            SpecialType.System_Object => SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)),
            SpecialType.System_Void => SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
            SpecialType.System_Int32 => SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
            SpecialType.System_Boolean => SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)),
            _ => (TypeSyntax)SyntaxFactory.ParseTypeName(typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
        };

        return typeSymbol.IsReferenceType && typeSymbol.NullableAnnotation == NullableAnnotation.Annotated
            ? SyntaxFactory.NullableType(syntax)
            : syntax;
    }

    public ITypeSymbol ResolveMockableDelegateType(Compilation compilation, ISymbol symbol)
    {
        var (returnTypeSymbol, parameterSymbols) = symbol switch
        {
            IPropertySymbol propertySymbol => (propertySymbol.Type, Array.Empty<IParameterSymbol>()),
            IFieldSymbol fieldSymbol => (fieldSymbol.Type, Array.Empty<IParameterSymbol>()),
            IMethodSymbol methodSymbol => (methodSymbol.ReturnType, methodSymbol.Parameters.ToArray()),
            _ => throw new ArgumentException("Unsupported symbol kind", nameof(symbol)),
        };

        return _delegateTypeResolver.ResolveDelegateType(compilation, returnTypeSymbol, parameterSymbols);
    }

    public AccessorListSyntax CreateAccessorList(IPropertySymbol propertySymbol)
    {
        var accessorDeclarations = new List<AccessorDeclarationSyntax>();
        if (propertySymbol.GetMethod != null)
            accessorDeclarations.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration, SyntaxFactory.Block()));
        if (propertySymbol.SetMethod != null)
            accessorDeclarations.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration, SyntaxFactory.Block()));

        return SyntaxFactory.AccessorList(SyntaxFactory.List(accessorDeclarations));
    }

    private AttributeSyntax CreateAttributeSyntax(AttributeData attributeData)
    {
        var containingNamespace = attributeData.AttributeClass?.ContainingNamespace;
        if (containingNamespace is { IsGlobalNamespace: false })
            _recordNamespaceName(containingNamespace.ToDisplayString());

        var attributeTypeName = SyntaxFactory.ParseName(attributeData.AttributeClass!.Name);
        var attributeArguments = attributeData.ConstructorArguments.Select(
            constructorArgument =>
                SyntaxFactory.AttributeArgument(
                    SyntaxFactory.LiteralExpression(
                        SyntaxKind.StringLiteralExpression,
                        SyntaxFactory.Literal(constructorArgument.Value?.ToString() ?? string.Empty)
                    )
                )
        );

        return attributeArguments.Any()
            ? SyntaxFactory.Attribute(attributeTypeName).WithArgumentList(
                SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(attributeArguments))
            )
            : SyntaxFactory.Attribute(attributeTypeName);
    }

    private static ExpressionSyntax CreateLiteralExpression(object? value)
    {
        return value == null
            ? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
            : SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(value.ToString() ?? string.Empty)
            );
    }
}
