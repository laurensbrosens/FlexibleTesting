using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using System.Collections.Generic;
using System.Linq;

namespace FlexibleTesting.Tasks;

public class FlexibleTestingRewriter(SemanticModel semanticModel, FlexibleTestingInstructions instructions, SyntaxGenerator generator)
    : CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var rewrittenNode = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
        var updatedNode = rewrittenNode.WithIdentifier(SyntaxFactory.Identifier(instructions.NewClassName));

        // Preserve partial modifier: ensure it's present if the source class is partial
        var hasPartial = updatedNode.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
        if (instructions.IsPartial && !hasPartial)
        {
            updatedNode = updatedNode.AddModifiers(SyntaxFactory.Token(SyntaxKind.PartialKeyword));
        }
        else if (!instructions.IsPartial && hasPartial)
        {
            updatedNode = updatedNode.WithModifiers(
                SyntaxFactory.TokenList(updatedNode.Modifiers.Where(m => !m.IsKind(SyntaxKind.PartialKeyword)))
            );
        }

        if (instructions.RemoveSealed)
        {
            updatedNode = updatedNode.WithModifiers(
                SyntaxFactory.TokenList(updatedNode.Modifiers.Where(m => !m.IsKind(SyntaxKind.SealedKeyword)))
            );
        }

        var baseType = semanticModel.GetDeclaredSymbol(node)?.BaseType;

        if ((instructions.MockInheritance || instructions.RecursiveMockInheritance) && updatedNode.BaseList != null)
        {
            var baseClassName = instructions.RecursiveMockInheritance && baseType != null
                ? FlexibleTestingGeneratedNames.GetGeneratedClassName(baseType.Name)
                : FlexibleTestingGeneratedNames.GetBaseGeneratedClassName(instructions.OldClassName);
            var newBaseList = updatedNode.BaseList.WithTypes(
                SyntaxFactory.SeparatedList(
                    new BaseTypeSyntax[] { SyntaxFactory.SimpleBaseType(SyntaxFactory.IdentifierName(baseClassName)) }
                )
            );
            updatedNode = updatedNode.WithBaseList(newBaseList);
        }

        if (node.ParameterList != null)
        {
            var primaryDependenciesParam = (ParameterSyntax)
                generator.ParameterDeclaration(instructions.DependenciesParameterName, generator.IdentifierName(instructions.DependenciesInterfaceName));
            updatedNode = updatedNode.WithParameterList(node.ParameterList.AddParameters(primaryDependenciesParam));
        }

        return updatedNode;
    }

    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var rewrittenNode = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;
        var updatedNode = rewrittenNode.WithIdentifier(SyntaxFactory.Identifier(instructions.NewClassName));
        var containingType = semanticModel.GetDeclaredSymbol(node)?.ContainingType;
        var hasRealBaseClass = containingType?.BaseType is { SpecialType: not SpecialType.System_Object };
        var baseTypeName = containingType?.BaseType?.Name;

        var dependenciesParamName = instructions.DependenciesParameterName;
        var dependenciesParam = (ParameterSyntax)
            generator.ParameterDeclaration(dependenciesParamName, generator.IdentifierName(instructions.DependenciesInterfaceName));

        var dependenciesAssignment = (StatementSyntax)
            generator.ExpressionStatement(
                generator.AssignmentStatement(
                    generator.IdentifierName(instructions.DependenciesFieldName),
                    generator.IdentifierName(dependenciesParamName)
                )
            );

        var statements = new List<StatementSyntax> { dependenciesAssignment };
        updatedNode = updatedNode.AddParameterListParameters(dependenciesParam);

        if ((instructions.MockInheritance || instructions.RecursiveMockInheritance) && hasRealBaseClass)
        {
            var baseDependencyParameters = new List<ParameterSyntax>();
            var baseDependencyArgumentExpressions = new List<ExpressionSyntax>();

            if (instructions.RecursiveMockInheritance && instructions.RecursiveBaseTypes.Count > 0)
            {
                for (var index = 0; index < instructions.RecursiveBaseTypes.Count; index++)
                {
                    var ancestorType = instructions.RecursiveBaseTypes[index];
                    var baseDepsParamName = GetRecursiveBaseDependenciesParameterName(index);
                    var baseDepsInterfaceName = FlexibleTestingGeneratedNames.GetDependenciesInterfaceName(ancestorType.Name);
                    baseDependencyParameters.Add(
                        (ParameterSyntax)generator.ParameterDeclaration(baseDepsParamName, generator.IdentifierName(baseDepsInterfaceName))
                    );
                    baseDependencyArgumentExpressions.Add(SyntaxFactory.IdentifierName(baseDepsParamName));
                }
            }
            else
            {
                var baseDepsParamName = FlexibleTestingGeneratedNames.BaseDependenciesParameterName;
                var baseDepsInterfaceName =
                    instructions.RecursiveMockInheritance && baseTypeName != null
                        ? FlexibleTestingGeneratedNames.GetDependenciesInterfaceName(baseTypeName)
                        : FlexibleTestingGeneratedNames.GetBaseDependenciesInterfaceName(instructions.OldClassName);
                baseDependencyParameters.Add(
                    (ParameterSyntax)generator.ParameterDeclaration(baseDepsParamName, generator.IdentifierName(baseDepsInterfaceName))
                );
                baseDependencyArgumentExpressions.Add(SyntaxFactory.IdentifierName(baseDepsParamName));
            }

            var firstBaseDependencyParameterName = baseDependencyParameters[0].Identifier.ValueText;

            if (updatedNode.Initializer?.IsKind(SyntaxKind.BaseConstructorInitializer) == true)
            {
                var baseArgs = updatedNode.Initializer.ArgumentList.Arguments.AddRange(
                    baseDependencyArgumentExpressions.Select(SyntaxFactory.Argument)
                );
                updatedNode = updatedNode.WithInitializer(
                    updatedNode.Initializer.WithArgumentList(updatedNode.Initializer.ArgumentList.WithArguments(baseArgs))
                );
            }
            else
            {
                updatedNode = updatedNode.WithInitializer(
                    SyntaxFactory.ConstructorInitializer(
                        SyntaxKind.BaseConstructorInitializer,
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SeparatedList(baseDependencyArgumentExpressions.Select(SyntaxFactory.Argument))
                        )
                    )
                );
            }

            updatedNode = updatedNode.AddParameterListParameters(baseDependencyParameters.ToArray());
            if (instructions.MockInheritance)
            {
                var baseAssignment = (StatementSyntax)
                generator.ExpressionStatement(
                    generator.AssignmentStatement(
                        generator.IdentifierName(FlexibleTestingGeneratedNames.BaseDependenciesFieldName),
                        generator.IdentifierName(firstBaseDependencyParameterName)
                    )
                );
                statements.Add(baseAssignment);
            }
        }

        return updatedNode.Body != null
            ? updatedNode.WithBody(updatedNode.Body.WithStatements(SyntaxFactory.List(statements.Concat(updatedNode.Body.Statements))))
            : updatedNode.WithBody(SyntaxFactory.Block(statements)).WithExpressionBody(null).WithSemicolonToken(default);
    }

    private static string GetRecursiveBaseDependenciesParameterName(int index)
    {
        if (index == 0)
            return FlexibleTestingGeneratedNames.BaseDependenciesParameterName;

        return $"base{string.Concat(Enumerable.Repeat("Base", index))}Dependencies";
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var symbol = semanticModel.GetDeclaredSymbol(node);
        var rewrittenNode = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;

        return (symbol != null && instructions.MethodsToMakePublic.Contains(symbol))
            ? (MethodDeclarationSyntax)generator.WithAccessibility(rewrittenNode, Accessibility.Public)
            : rewrittenNode;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var symbol = semanticModel.GetSymbolInfo(node).Symbol;
        if (symbol != null && instructions.DependencyMemberNames.TryGetValue(symbol, out var dependencyName))
        {
            return generator
                .InvocationExpression(
                    generator.MemberAccessExpression(generator.IdentifierName(instructions.DependenciesFieldName), dependencyName),
                    node.ArgumentList.Arguments.Select(a => a.Expression)
                )
                .WithTriviaFrom(node);
        }
        return base.VisitInvocationExpression(node);
    }

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var constructor = semanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
        var containingType = constructor?.ContainingType;
        if (containingType != null && instructions.MockClasses.Any(t => SymbolEqualityComparer.Default.Equals(t, containingType)))
        {
            var dependencyName = containingType.Name;
            var arguments = node.ArgumentList?.Arguments.Select(a => a.Expression) ?? Enumerable.Empty<ExpressionSyntax>();
            return generator
                .InvocationExpression(
                    generator.MemberAccessExpression(generator.IdentifierName(instructions.DependenciesFieldName), dependencyName),
                    arguments
                )
                .WithTriviaFrom(node);
        }
        return base.VisitObjectCreationExpression(node);
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var symbol = semanticModel.GetSymbolInfo(node).Symbol;
        if (symbol != null && instructions.DependencyMemberNames.TryGetValue(symbol, out var dependencyName))
        {
            if (node.Parent is AssignmentExpressionSyntax assignment && assignment.Left == node)
            {
                return generator.MemberAccessExpression(generator.IdentifierName(instructions.DependenciesFieldName), dependencyName)
                    .WithTriviaFrom(node);
            }
            
            return generator
                .InvocationExpression(
                    generator.MemberAccessExpression(generator.IdentifierName(instructions.DependenciesFieldName), dependencyName)
                )
                .WithTriviaFrom(node);
        }
        return base.VisitMemberAccessExpression(node);
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var symbol = semanticModel.GetSymbolInfo(node).Symbol;
        
        if (symbol != null && instructions.DependencyMemberNames.TryGetValue(symbol, out var dependencyName))
        {
            if (node.Parent is AssignmentExpressionSyntax assignment && assignment.Left == node)
            {
                return generator.MemberAccessExpression(generator.IdentifierName(instructions.DependenciesFieldName), dependencyName)
                    .WithTriviaFrom(node);
            }

            if (symbol is IPropertySymbol propertySymbol && instructions.MockProperties.Contains(propertySymbol))
            {
                return generator
                    .InvocationExpression(
                        generator.MemberAccessExpression(generator.IdentifierName(instructions.DependenciesFieldName), dependencyName)
                    )
                    .WithTriviaFrom(node);
            }
        }

        if (symbol is INamedTypeSymbol namedType && instructions.MockClasses.Any(t => SymbolEqualityComparer.Default.Equals(t, namedType)))
            return SyntaxFactory.IdentifierName(FlexibleTestingGeneratedNames.GetMockClassInterfaceName(namedType.Name)).WithTriviaFrom(node);

        return base.VisitIdentifierName(node);
    }

    public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node)
    {
        var symbol = semanticModel.GetSymbolInfo(node).Symbol;
        if (symbol is INamedTypeSymbol namedType && instructions.MockClasses.Any(t => SymbolEqualityComparer.Default.Equals(t, namedType)))
            return SyntaxFactory.IdentifierName(FlexibleTestingGeneratedNames.GetMockClassInterfaceName(namedType.Name)).WithTriviaFrom(node);

        return base.VisitQualifiedName(node);
    }

    public override SyntaxNode? VisitAliasQualifiedName(AliasQualifiedNameSyntax node)
    {
        var symbol = semanticModel.GetSymbolInfo(node).Symbol;
        if (symbol is INamedTypeSymbol namedType && instructions.MockClasses.Any(t => SymbolEqualityComparer.Default.Equals(t, namedType)))
            return SyntaxFactory.IdentifierName(FlexibleTestingGeneratedNames.GetMockClassInterfaceName(namedType.Name)).WithTriviaFrom(node);

        return base.VisitAliasQualifiedName(node);
    }

}
