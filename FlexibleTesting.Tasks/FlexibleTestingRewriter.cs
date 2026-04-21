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
    public bool NeedsCallerMemberName { get; private set; }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var rewrittenNode = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
        var updatedNode = rewrittenNode.WithIdentifier(SyntaxFactory.Identifier(instructions.NewClassName));

        if (instructions.MockInheritance && updatedNode.BaseList != null)
        {
            var baseClassName = $"{instructions.OldClassName}Base_G";
            var newBaseList = updatedNode.BaseList.WithTypes(
                SyntaxFactory.SeparatedList(
                    new BaseTypeSyntax[] { SyntaxFactory.SimpleBaseType(SyntaxFactory.IdentifierName(baseClassName)) }
                )
            );
            updatedNode = updatedNode.WithBaseList(newBaseList);
        }

        return updatedNode;
    }

    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var rewrittenNode = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;
        var updatedNode = rewrittenNode.WithIdentifier(SyntaxFactory.Identifier(instructions.NewClassName));

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

        if (instructions.MockInheritance)
        {
            var baseDepsParamName = "baseDependencies";
            var baseDepsParam = (ParameterSyntax)
                generator.ParameterDeclaration(
                    baseDepsParamName,
                    generator.IdentifierName($"IAuto{instructions.OldClassName}BaseDependencies")
                );

            if (updatedNode.Initializer?.IsKind(SyntaxKind.BaseConstructorInitializer) == true)
            {
                var baseArgs = updatedNode.Initializer.ArgumentList.Arguments.Add(
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName(baseDepsParamName))
                );
                updatedNode = updatedNode.WithInitializer(
                    updatedNode.Initializer.WithArgumentList(updatedNode.Initializer.ArgumentList.WithArguments(baseArgs))
                );
            }
            else
            {
                updatedNode = updatedNode.WithInitializer(
                    SyntaxFactory.ConstructorInitializer(SyntaxKind.BaseConstructorInitializer, 
                        SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(baseDepsParamName)))))
                );
            }

            var baseAssignment = (StatementSyntax)
                generator.ExpressionStatement(
                    generator.AssignmentStatement(
                        generator.IdentifierName("_baseDependencies"),
                        generator.IdentifierName(baseDepsParamName)
                    )
                );
            updatedNode = updatedNode.AddParameterListParameters(baseDepsParam);
            statements.Add(baseAssignment);
        }

        return updatedNode.Body != null
            ? updatedNode.WithBody(updatedNode.Body.WithStatements(SyntaxFactory.List(statements.Concat(updatedNode.Body.Statements))))
            : updatedNode.WithBody(SyntaxFactory.Block(statements)).WithExpressionBody(null).WithSemicolonToken(default);
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
            CheckForCallerMemberName(symbol);
            return generator
                .InvocationExpression(
                    generator.MemberAccessExpression(generator.IdentifierName(instructions.DependenciesFieldName), dependencyName),
                    node.ArgumentList.Arguments.Select(a => a.Expression)
                )
                .WithTriviaFrom(node);
        }
        return base.VisitInvocationExpression(node);
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var symbol = semanticModel.GetSymbolInfo(node).Symbol;
        if (symbol != null && instructions.DependencyMemberNames.TryGetValue(symbol, out var dependencyName))
        {
            CheckForCallerMemberName(symbol);
            return generator
                .InvocationExpression(
                    generator.MemberAccessExpression(generator.IdentifierName(instructions.DependenciesFieldName), dependencyName)
                )
                .WithTriviaFrom(node);
        }
        return base.VisitMemberAccessExpression(node);
    }

    private void CheckForCallerMemberName(ISymbol symbol)
    {
        if (
            symbol is IMethodSymbol method
            && method.Parameters.Any(p => p.GetAttributes().Any(a => a.AttributeClass?.Name == "CallerMemberNameAttribute"))
        )
        {
            NeedsCallerMemberName = true;
        }
    }
}
