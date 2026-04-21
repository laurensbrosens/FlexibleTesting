using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace FlexibleTesting.Tasks;

/// <summary>
/// Handles surgical rewrites of the syntax tree, such as renaming classes,
/// replacing mockable calls, and updating modifiers.
/// </summary>
public class FlexibleTestingRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _semanticModel;
    private readonly FlexibleTestingInstructions _instructions;
    private readonly SyntaxGenerator _generator;

    public bool NeedsCallerMemberName { get; private set; }

    public FlexibleTestingRewriter(SemanticModel semanticModel, FlexibleTestingInstructions instructions, SyntaxGenerator generator)
    {
        _semanticModel = semanticModel;
        _instructions = instructions;
        _generator = generator;
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var rewrittenNode = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;

        // 1. Rename the class
        var updatedNode = rewrittenNode.WithIdentifier(SyntaxFactory.Identifier(_instructions.NewClassName));

        // 2. Update base class if MockInheritance is enabled
        if (_instructions.MockInheritance && updatedNode.BaseList != null)
        {
            var baseClassName = $"{_instructions.OldClassName}Base_G";
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

        // Rename constructor to match new class name
        var updatedNode = rewrittenNode.WithIdentifier(SyntaxFactory.Identifier(_instructions.NewClassName));

        // Inject dependencies
        var paramName = _instructions.DependenciesParameterName;
        if (updatedNode.ParameterList.Parameters.Any(p => p.Identifier.Text == paramName))
        {
            paramName += "2";
        }

        var newParam = (ParameterSyntax)
            _generator.ParameterDeclaration(paramName, _generator.IdentifierName(_instructions.DependenciesInterfaceName));

        var assignment = (StatementSyntax)
            _generator.ExpressionStatement(
                _generator.AssignmentStatement(
                    _generator.IdentifierName(_instructions.DependenciesFieldName),
                    _generator.IdentifierName(paramName)
                )
            );

        var statements = new List<StatementSyntax>();

        // First add the normal dependencies
        updatedNode = updatedNode.AddParameterListParameters(newParam);
        statements.Add(assignment);

        if (_instructions.MockInheritance)
        {
            var baseDepsParamName = "baseDependencies";
            if (updatedNode.ParameterList.Parameters.Any(p => p.Identifier.Text == baseDepsParamName))
            {
                baseDepsParamName += "2";
            }

            var baseDepsInterface = $"IAuto{_instructions.OldClassName}BaseDependencies";
            var baseParam = (ParameterSyntax)
                _generator.ParameterDeclaration(baseDepsParamName, _generator.IdentifierName(baseDepsInterface));

            // Update base() call
            if (updatedNode.Initializer != null && updatedNode.Initializer.IsKind(SyntaxKind.BaseConstructorInitializer))
            {
                var baseArgs = updatedNode.Initializer.ArgumentList.Arguments.Add(
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName(baseDepsParamName))
                );
                updatedNode = updatedNode.WithInitializer(
                    updatedNode.Initializer.WithArgumentList(updatedNode.Initializer.ArgumentList.WithArguments(baseArgs))
                );
            }

            var baseAssignment = (StatementSyntax)
                _generator.ExpressionStatement(
                    _generator.AssignmentStatement(
                        _generator.IdentifierName("_baseDependencies"),
                        _generator.IdentifierName(baseDepsParamName)
                    )
                );

            // Then add base dependencies so they appear last
            updatedNode = updatedNode.AddParameterListParameters(baseParam);
            statements.Add(baseAssignment);
        }

        if (updatedNode.Body != null)
        {
            updatedNode = updatedNode.WithBody(
                updatedNode.Body.WithStatements(SyntaxFactory.List(statements.Concat(updatedNode.Body.Statements)))
            );
        }
        else
        {
            updatedNode = updatedNode.WithBody(SyntaxFactory.Block(statements)).WithExpressionBody(null).WithSemicolonToken(default);
        }

        return updatedNode;
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var symbol = _semanticModel.GetDeclaredSymbol(node);

        var rewrittenNode = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;

        if (symbol != null && _instructions.MethodsToMakePublic.Contains(symbol))
        {
            rewrittenNode = MakePublic(rewrittenNode);
        }

        return rewrittenNode;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var symbol = _semanticModel.GetSymbolInfo(node).Symbol;
        if (symbol != null && _instructions.DependencyMemberNames.TryGetValue(symbol, out var depName))
        {
            CheckForCallerMemberName(symbol);

            var replacement = _generator.InvocationExpression(
                _generator.MemberAccessExpression(_generator.IdentifierName(_instructions.DependenciesFieldName), depName),
                node.ArgumentList.Arguments.Select(a => a.Expression)
            );
            return replacement.WithTriviaFrom(node);
        }

        return base.VisitInvocationExpression(node);
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var symbol = _semanticModel.GetSymbolInfo(node).Symbol;
        if (symbol != null && _instructions.DependencyMemberNames.TryGetValue(symbol, out var depName))
        {
            CheckForCallerMemberName(symbol);

            // If it's a property/field, replace with dependency call
            var replacement = _generator.InvocationExpression(
                _generator.MemberAccessExpression(_generator.IdentifierName(_instructions.DependenciesFieldName), depName)
            );
            return replacement.WithTriviaFrom(node);
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

    private MethodDeclarationSyntax MakePublic(MethodDeclarationSyntax node)
    {
        return (MethodDeclarationSyntax)_generator.WithAccessibility(node, Accessibility.Public);
    }
}
