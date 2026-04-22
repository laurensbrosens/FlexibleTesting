using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using System.Collections.Generic;
using System.Linq;

namespace FlexibleTesting.Tasks;

internal sealed class BaseInheritanceGenerator : IBaseInheritanceGenerator
{
    private readonly IFlexibleTestingSyntaxFactory _syntaxFactory;
    private readonly IFlexibleTestingNamePolicy _namePolicy;

    public BaseInheritanceGenerator(IFlexibleTestingSyntaxFactory syntaxFactory, IFlexibleTestingNamePolicy namePolicy)
    {
        _syntaxFactory = syntaxFactory;
        _namePolicy = namePolicy;
    }

    public bool TryAddBaseInheritanceMembers(
        SyntaxEditor syntaxEditor,
        ClassDeclarationSyntax classNode,
        FlexibleTestingInstructions instructions,
        SemanticModel semanticModel,
        SyntaxGenerator syntaxGenerator
    )
    {
        var baseTypeSymbol = instructions.TargetType.BaseType;
        if (baseTypeSymbol == null || baseTypeSymbol.SpecialType == SpecialType.System_Object)
            return false;

        var usedBaseMembers = ExtractUsedBaseMembers(
            (ClassDeclarationSyntax)instructions.TargetType.DeclaringSyntaxReferences[0].GetSyntax(),
            instructions.TargetType,
            semanticModel
        );
        if (!usedBaseMembers.Any())
            return false;

        var baseDependenciesInterfaceName = _namePolicy.GetBaseDependenciesInterfaceName(instructions.OldClassName);
        syntaxEditor.InsertBefore(
            classNode,
            BuildBaseClassReplacement(
                syntaxGenerator,
                instructions.OldClassName,
                baseTypeSymbol,
                usedBaseMembers,
                baseDependenciesInterfaceName,
                instructions.MethodsToMakePublic
            )
        );
        syntaxEditor.InsertBefore(
            classNode,
            BuildBaseDependenciesInterface(syntaxGenerator, instructions.OldClassName, usedBaseMembers)
        );

        return true;
    }

    private SyntaxNode BuildBaseClassReplacement(
        SyntaxGenerator syntaxGenerator,
        string oldClassName,
        INamedTypeSymbol baseTypeSymbol,
        List<ISymbol> usedMembers,
        string baseDependenciesInterfaceName,
        HashSet<IMethodSymbol> publicMethods
    )
    {
        var generatedBaseClassName = _namePolicy.GetBaseGeneratedClassName(oldClassName);
        var baseClassMembers = new List<MemberDeclarationSyntax>();

        var originalBaseConstructor = baseTypeSymbol.Constructors.OrderByDescending(constructor => constructor.Parameters.Length).FirstOrDefault();
        var constructorParameters = new List<SyntaxNode>();
        var constructorArguments = new List<SyntaxNode>();

        if (originalBaseConstructor != null)
        {
            foreach (var constructorParameter in originalBaseConstructor.Parameters)
            {
                constructorParameters.Add(_syntaxFactory.CreateParameter(syntaxGenerator, constructorParameter));
                constructorArguments.Add(SyntaxFactory.IdentifierName(constructorParameter.Name));
            }
        }

        constructorParameters.Add(
            syntaxGenerator.ParameterDeclaration(
                _namePolicy.BaseDependenciesParameterName,
                syntaxGenerator.IdentifierName(baseDependenciesInterfaceName)
            )
        );

        var constructorDeclaration = (ConstructorDeclarationSyntax)
            syntaxGenerator.ConstructorDeclaration(
                generatedBaseClassName,
                parameters: constructorParameters,
                accessibility: Accessibility.Public
            );
        constructorDeclaration = constructorDeclaration.WithBody(
            SyntaxFactory.Block(
                SyntaxFactory.ParseStatement(
                    $"{_namePolicy.BaseDependenciesFieldName} = {_namePolicy.BaseDependenciesParameterName};"
                )
            )
        );
        baseClassMembers.Add(constructorDeclaration);

        baseClassMembers.Add(
            (FieldDeclarationSyntax)
                syntaxGenerator.FieldDeclaration(
                    _namePolicy.BaseDependenciesFieldName,
                    syntaxGenerator.IdentifierName(baseDependenciesInterfaceName),
                    Accessibility.Private,
                    DeclarationModifiers.ReadOnly
                )
        );

        foreach (var usedMember in usedMembers)
        {
            if (usedMember is IMethodSymbol methodSymbol && methodSymbol.MethodKind != MethodKind.Constructor)
                baseClassMembers.Add(CreateBaseMethodStub(syntaxGenerator, methodSymbol, publicMethods.Contains(methodSymbol)));
            else if (usedMember is IPropertySymbol propertySymbol)
                baseClassMembers.Add(CreateBasePropertyStub(syntaxGenerator, propertySymbol));
        }

        return syntaxGenerator.ClassDeclaration(
            generatedBaseClassName,
            accessibility: Accessibility.Public,
            members: baseClassMembers
        );
    }

    private MethodDeclarationSyntax CreateBaseMethodStub(
        SyntaxGenerator syntaxGenerator,
        IMethodSymbol methodSymbol,
        bool shouldBePublic
    )
    {
        var parameterDeclarations = methodSymbol.Parameters.Select(
            parameterSymbol => _syntaxFactory.CreateParameter(syntaxGenerator, parameterSymbol)
        );
        var dependencyMemberArguments = methodSymbol.Parameters.Select(
            parameterSymbol => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(parameterSymbol.Name))
        );
        var dependencyMethodInvocation = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(_namePolicy.BaseDependenciesFieldName),
                SyntaxFactory.IdentifierName(methodSymbol.Name)
            ),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(dependencyMemberArguments))
        );

        var methodAccessibility = shouldBePublic
            ? Accessibility.Public
            : (methodSymbol.DeclaredAccessibility == Accessibility.Protected ? Accessibility.Protected : Accessibility.Public);

        var methodDeclaration = (MethodDeclarationSyntax)
            syntaxGenerator.MethodDeclaration(
                methodSymbol.Name,
                parameters: parameterDeclarations,
                returnType: _syntaxFactory.CreateTypeSyntax(methodSymbol.ReturnType),
                accessibility: methodAccessibility,
                modifiers: DeclarationModifiers.Virtual
            );

        foreach (var attributeSyntax in _syntaxFactory.CreateAttributes(methodSymbol))
            methodDeclaration = (MethodDeclarationSyntax)syntaxGenerator.AddAttributes(methodDeclaration, attributeSyntax);

        var methodBodyStatement =
            methodSymbol.ReturnType.SpecialType == SpecialType.System_Void
                ? (StatementSyntax)SyntaxFactory.ExpressionStatement(dependencyMethodInvocation)
                : SyntaxFactory.ReturnStatement(dependencyMethodInvocation);

        return methodDeclaration.WithBody(SyntaxFactory.Block(methodBodyStatement));
    }

    private PropertyDeclarationSyntax CreateBasePropertyStub(SyntaxGenerator syntaxGenerator, IPropertySymbol propertySymbol)
    {
        var propertyDeclaration = (PropertyDeclarationSyntax)
            syntaxGenerator.PropertyDeclaration(
                propertySymbol.Name,
                _syntaxFactory.CreateTypeSyntax(propertySymbol.Type),
                accessibility: Accessibility.Public,
                modifiers: DeclarationModifiers.Virtual
            );

        propertyDeclaration = propertyDeclaration.WithAccessorList(_syntaxFactory.CreateAccessorList(propertySymbol));
        return propertyDeclaration;
    }

    private SyntaxNode BuildBaseDependenciesInterface(
        SyntaxGenerator syntaxGenerator,
        string oldClassName,
        List<ISymbol> usedMembers
    )
    {
        var interfaceMembers = usedMembers.Select(usedMember =>
        {
            if (usedMember is IMethodSymbol methodSymbol && methodSymbol.MethodKind != MethodKind.Constructor)
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

                return (MemberDeclarationSyntax)methodDeclaration;
            }

            var propertySymbol = (IPropertySymbol)usedMember;
            var propertyDeclaration = (PropertyDeclarationSyntax)
                syntaxGenerator.PropertyDeclaration(
                    propertySymbol.Name,
                    _syntaxFactory.CreateTypeSyntax(propertySymbol.Type),
                    accessibility: Accessibility.Public
                );

            foreach (var attributeSyntax in _syntaxFactory.CreateAttributes(propertySymbol))
                propertyDeclaration = (PropertyDeclarationSyntax)syntaxGenerator.AddAttributes(propertyDeclaration, attributeSyntax);

            return (MemberDeclarationSyntax)propertyDeclaration;
        });

        return syntaxGenerator
            .InterfaceDeclaration(
                _namePolicy.GetBaseDependenciesInterfaceName(oldClassName),
                accessibility: Accessibility.Public,
                members: interfaceMembers
            )
            .WithLeadingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.Comment("/// <summary>Mock this using NSubstitute</summary>")));
    }

    private static List<ISymbol> ExtractUsedBaseMembers(
        ClassDeclarationSyntax derivedNode,
        INamedTypeSymbol targetSymbol,
        SemanticModel semanticModel
    )
    {
        var baseTypeSymbol = targetSymbol.BaseType;
        if (baseTypeSymbol == null || baseTypeSymbol.SpecialType == SpecialType.System_Object)
            return new List<ISymbol>();

        var usedSymbols = new List<ISymbol>();
        foreach (var syntaxNode in derivedNode.DescendantNodes())
        {
            var symbol = syntaxNode switch
            {
                InvocationExpressionSyntax invocationExpression => semanticModel.GetSymbolInfo(invocationExpression.Expression).Symbol,
                MemberAccessExpressionSyntax memberAccessExpression => semanticModel.GetSymbolInfo(memberAccessExpression).Symbol,
                IdentifierNameSyntax identifierName when identifierName.Parent is not MemberAccessExpressionSyntax
                    => semanticModel.GetSymbolInfo(identifierName).Symbol,
                _ => null,
            };

            if (symbol != null && baseTypeSymbol.GetMembers().Contains(symbol, SymbolEqualityComparer.Default) && !usedSymbols.Contains(symbol, SymbolEqualityComparer.Default))
                usedSymbols.Add(symbol);
        }

        return usedSymbols;
    }
}
