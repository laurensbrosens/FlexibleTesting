using FlexibleTesting.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;

namespace FlexibleTesting;

public class ClassRenamer : CSharpSyntaxRewriter
{
    private readonly SemanticModel _semanticModel;
    private readonly string _oldName;
    private readonly string _newName;
    private readonly List<string> _methodsToMakePublicSignatures;

    public ClassRenamer(SemanticModel semanticModel, string oldName, string newName, IEnumerable<IMethodSymbol> methodsToMakePublic)
    {
        _semanticModel = semanticModel;
        _oldName = oldName;
        _newName = newName;
        // We zetten de symbolen direct om naar signatures voor snelle vergelijking
        _methodsToMakePublicSignatures = methodsToMakePublic.Select(m => m.ToSignatureString()).ToList();
    }

    public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        // 1. Bezoek eerst de kinderen (methoden) zodat de SemanticModel ze nog kan vinden
        var visitedNode = (ClassDeclarationSyntax)base.VisitClassDeclaration(node);

        // 2. Pas daarna de naam van de klasse aan
        if (visitedNode.Identifier.Text == _oldName)
        {
            visitedNode = visitedNode.WithIdentifier(SyntaxFactory.Identifier(_newName));
        }

        return visitedNode;
    }

    public override SyntaxNode VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        // Constructors moeten ook de nieuwe klassenaam krijgen
        if (node.Identifier.Text == _oldName)
        {
            node = node.WithIdentifier(SyntaxFactory.Identifier(_newName));
        }
        return base.VisitConstructorDeclaration(node);
    }

    public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var symbol = _semanticModel.GetDeclaredSymbol(node);
        if (symbol == null)
            return base.VisitMethodDeclaration(node);

        if (_methodsToMakePublicSignatures.Contains(symbol.ToSignatureString()))
        {
            // 1. Bewaar de originele tekst van de declaratie (zonder de body) voor het commentaar
            // We pakken de tekst van het begin van de node tot aan het begin van de body
            var originalDeclaration = node.WithBody(null).WithSemicolonToken(default).ToString().Trim();
            var commentTrivia = SyntaxFactory.Comment($" // Original: {originalDeclaration}");

            // 2. Behoud de inspringing (zoals in de vorige stap)
            var leadingTrivia = node.Modifiers.Count > 0 ? node.Modifiers.First().LeadingTrivia : node.ReturnType.GetLeadingTrivia();

            // 3. Filter modifiers
            var otherModifiers = node
                .Modifiers.Where(m => !m.IsKind(SyntaxKind.PrivateKeyword) && !m.IsKind(SyntaxKind.ProtectedKeyword))
                .ToList();

            var publicToken = SyntaxFactory
                .Token(SyntaxKind.PublicKeyword)
                .WithLeadingTrivia(leadingTrivia)
                .WithTrailingTrivia(SyntaxFactory.Space);

            // 4. Voeg het commentaar toe aan het einde van de parameterlijst (ParameterList.GetTrailingTrivia)
            // of aan de SemicolonToken als het een abstracte/interface methode is.
            var updatedNode = node.WithModifiers(SyntaxFactory.TokenList(otherModifiers.Prepend(publicToken)))
                .WithReturnType(node.ReturnType.WithLeadingTrivia(SyntaxFactory.TriviaList()));

            // We plakken het commentaar achter de parameterlijst
            var newTrailingTrivia = updatedNode.ParameterList.GetTrailingTrivia().Insert(0, commentTrivia);

            return updatedNode.WithParameterList(updatedNode.ParameterList.WithTrailingTrivia(newTrailingTrivia));
        }

        return base.VisitMethodDeclaration(node);
    }
}
