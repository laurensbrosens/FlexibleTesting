using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using System.Collections.Generic;
using System.Linq;

namespace FlexibleTesting.Tasks;

internal sealed class DependencyInterfaceGenerator : IDependencyInterfaceGenerator
{
    private readonly IFlexibleTestingSyntaxFactory _syntaxFactory;
    private readonly IFlexibleTestingNamePolicy _namePolicy;

    public DependencyInterfaceGenerator(IFlexibleTestingSyntaxFactory syntaxFactory, IFlexibleTestingNamePolicy namePolicy)
    {
        _syntaxFactory = syntaxFactory;
        _namePolicy = namePolicy;
    }

    public SyntaxNode BuildDependenciesInterface(
        SyntaxGenerator syntaxGenerator,
        FlexibleTestingInstructions instructions,
        Compilation compilation
    )
    {
        var interfaceMembers = instructions.AllMocks.Select(mockableSymbol =>
        {
            var dependencyMemberName = instructions.DependencyMemberNames[mockableSymbol];
            if (mockableSymbol is IMethodSymbol methodSymbol)
                return BuildMethodInterfaceMember(syntaxGenerator, methodSymbol, dependencyMemberName);

            return BuildPropertyInterfaceMember(syntaxGenerator, compilation, mockableSymbol, dependencyMemberName);
        });

        var classMockMembers = instructions.MockClasses
            .OrderBy(mockedTypeSymbol => mockedTypeSymbol.Name, System.StringComparer.Ordinal)
            .SelectMany(mockedTypeSymbol =>
        {
            var mockClassInterfaceName = _namePolicy.GetMockClassInterfaceName(mockedTypeSymbol.Name);
            return instructions.MockClassConstructors
                .Where(constructorSymbol => SymbolSignatureComparer.Default.Equals(constructorSymbol.ContainingType, mockedTypeSymbol))
                .OrderBy(constructorSymbol => constructorSymbol.Parameters.Length)
                .Select(
                    constructorSymbol =>
                        BuildConstructorInterfaceMember(
                            syntaxGenerator,
                            constructorSymbol,
                            mockClassInterfaceName,
                            mockedTypeSymbol.Name
                        )
                );
        });

        return syntaxGenerator
            .InterfaceDeclaration(
                _namePolicy.GetDependenciesInterfaceName(instructions.OldClassName),
                accessibility: Accessibility.Public,
                members: interfaceMembers.Concat(classMockMembers)
            )
            .WithLeadingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.Comment("/// <summary>Mock this using NSubstitute</summary>")));
    }

    public SyntaxNode BuildMockClassInterface(SyntaxGenerator syntaxGenerator, INamedTypeSymbol mockedTypeSymbol)
    {
        var interfaceMembers = new List<MemberDeclarationSyntax>();

        foreach (var memberSymbol in mockedTypeSymbol.GetMembers().Where(member => !member.IsStatic))
        {
            switch (memberSymbol)
            {
                case IMethodSymbol methodSymbol when methodSymbol.MethodKind == MethodKind.Ordinary:
                    interfaceMembers.Add(BuildMethodMember(syntaxGenerator, methodSymbol));
                    break;
                case IPropertySymbol propertySymbol:
                    interfaceMembers.Add(BuildPropertyMember(syntaxGenerator, propertySymbol));
                    break;
            }
        }

        return syntaxGenerator
            .InterfaceDeclaration(
                _namePolicy.GetMockClassInterfaceName(mockedTypeSymbol.Name),
                accessibility: Accessibility.Public,
                members: interfaceMembers
            )
            .WithLeadingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.Comment("/// <summary>Mock this using NSubstitute</summary>")));
    }

    private MemberDeclarationSyntax BuildMethodInterfaceMember(
        SyntaxGenerator syntaxGenerator,
        IMethodSymbol methodSymbol,
        string dependencyMemberName
    )
    {
        var parameterDeclarations = methodSymbol.Parameters.Select(
            parameterSymbol => _syntaxFactory.CreateParameter(syntaxGenerator, parameterSymbol)
        );
        var methodDeclaration = (MethodDeclarationSyntax)
            syntaxGenerator.MethodDeclaration(
                dependencyMemberName,
                parameters: parameterDeclarations,
                returnType: syntaxGenerator.TypeExpression(methodSymbol.ReturnType),
                accessibility: Accessibility.Public
            );

        foreach (var attributeSyntax in _syntaxFactory.CreateAttributes(methodSymbol))
            methodDeclaration = (MethodDeclarationSyntax)syntaxGenerator.AddAttributes(methodDeclaration, attributeSyntax);

        return methodDeclaration;
    }

    private MemberDeclarationSyntax BuildPropertyInterfaceMember(
        SyntaxGenerator syntaxGenerator,
        Compilation compilation,
        ISymbol mockableSymbol,
        string dependencyMemberName
    )
    {
        var type = mockableSymbol switch
        {
            IPropertySymbol propertySymbol => propertySymbol.Type,
            IFieldSymbol fieldSymbol => fieldSymbol.Type,
            _ => throw new System.ArgumentException("Unsupported symbol kind", nameof(mockableSymbol)),
        };

        var propertyDeclaration = (PropertyDeclarationSyntax)
            syntaxGenerator.PropertyDeclaration(
                dependencyMemberName,
                syntaxGenerator.TypeExpression(type),
                accessibility: Accessibility.Public
            );

        // Add both get and set to make it mockable/assignable
        propertyDeclaration = propertyDeclaration.WithAccessorList(
            SyntaxFactory.AccessorList(
                SyntaxFactory.List(
                    new[]
                    {
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                    }
                )
            )
        );

        foreach (var attributeSyntax in _syntaxFactory.CreateAttributes(mockableSymbol))
            propertyDeclaration = (PropertyDeclarationSyntax)syntaxGenerator.AddAttributes(propertyDeclaration, attributeSyntax);

        return propertyDeclaration;
    }

    private MemberDeclarationSyntax BuildConstructorInterfaceMember(
        SyntaxGenerator syntaxGenerator,
        IMethodSymbol constructorSymbol,
        string mockClassInterfaceName,
        string mockedTypeName
    )
    {
        var parameterDeclarations = constructorSymbol.Parameters.Select(
            parameterSymbol => _syntaxFactory.CreateParameter(syntaxGenerator, parameterSymbol)
        );
        var constructorDeclaration = (MethodDeclarationSyntax)
            syntaxGenerator.MethodDeclaration(
                mockedTypeName,
                parameters: parameterDeclarations,
                returnType: SyntaxFactory.ParseTypeName(mockClassInterfaceName),
                accessibility: Accessibility.Public
            );

        foreach (var attributeSyntax in _syntaxFactory.CreateAttributes(constructorSymbol))
            constructorDeclaration = (MethodDeclarationSyntax)syntaxGenerator.AddAttributes(constructorDeclaration, attributeSyntax);

        return constructorDeclaration;
    }

    private MemberDeclarationSyntax BuildMethodMember(SyntaxGenerator syntaxGenerator, IMethodSymbol methodSymbol)
    {
        var parameterDeclarations = methodSymbol.Parameters.Select(
            parameterSymbol => _syntaxFactory.CreateParameter(syntaxGenerator, parameterSymbol)
        );
        var methodDeclaration = (MethodDeclarationSyntax)
            syntaxGenerator.MethodDeclaration(
                methodSymbol.Name,
                parameters: parameterDeclarations,
                returnType: _syntaxFactory.CreateTypeSyntax(methodSymbol.ReturnType),
                accessibility: Accessibility.Public
            );

        foreach (var attributeSyntax in _syntaxFactory.CreateAttributes(methodSymbol))
            methodDeclaration = (MethodDeclarationSyntax)syntaxGenerator.AddAttributes(methodDeclaration, attributeSyntax);

        return methodDeclaration;
    }

    private MemberDeclarationSyntax BuildPropertyMember(SyntaxGenerator syntaxGenerator, IPropertySymbol propertySymbol)
    {
        var propertyDeclaration = (PropertyDeclarationSyntax)
            syntaxGenerator.PropertyDeclaration(
                propertySymbol.Name,
                _syntaxFactory.CreateTypeSyntax(propertySymbol.Type),
                accessibility: Accessibility.Public
            );

        propertyDeclaration = propertyDeclaration.WithAccessorList(_syntaxFactory.CreateAccessorList(propertySymbol));

        foreach (var attributeSyntax in _syntaxFactory.CreateAttributes(propertySymbol))
            propertyDeclaration = (PropertyDeclarationSyntax)syntaxGenerator.AddAttributes(propertyDeclaration, attributeSyntax);

        return propertyDeclaration;
    }
}
