using FlexibleTesting.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;

namespace FlexibleTesting;

/// <summary>
/// Documentation: https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/get-started/syntax-transformation#transform-trees-using-syntaxrewriters
/// The reason these methods return a SyntaxNode instead of the more specific type is because you can change the node to a different type to replace it.
/// Also, all of these overrides are recursive, so you probably want to do base.Visit at the end of each method.
/// Returning null deletes the node.
///
/// Better alternatives could be:
/// * DocumentEditor
/// * SyntaxGenerator // Language agnostic syntax generator
/// * Renamer // Seems identical to the Rename functionality in Visual Studio
/// </summary>
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
        _methodsToMakePublicSignatures = methodsToMakePublic.Select(m => m.ToSignatureString()).ToList();
    }

    public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var classDecl = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
        if (classDecl.Identifier.Text == _oldName)
        {
            classDecl = classDecl.WithIdentifier(SyntaxFactory.Identifier(_newName));
        }

        return classDecl;
    }

    public override SyntaxNode VisitConstructorDeclaration(ConstructorDeclarationSyntax constructor)
    {
        if (constructor.Identifier.Text == _oldName)
        {
            constructor = constructor.WithIdentifier(SyntaxFactory.Identifier(_newName));
        }
        return base.VisitConstructorDeclaration(constructor)!;
    }

    public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var symbol = _semanticModel.GetDeclaredSymbol(node);
        if (symbol == null)
        {
            return base.VisitMethodDeclaration(node);
        }

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
/* Is this better?
public async Task<Document> MakeSignaturesPublicAsync(Document document, HashSet<string> targetSignatures)
{
    // 1. Initialiseer de editor en generator
    var editor = await DocumentEditor.CreateAsync(document);
    var generator = editor.Generator;
    var semanticModel = await document.GetSemanticModelAsync();
    var root = await document.GetSyntaxRootAsync();

    // 2. Zoek alle methode-declaraties in het document
    var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();

    foreach (var method in methods)
    {
        var symbol = semanticModel.GetDeclaredSymbol(method);
        if (symbol == null) continue;

        // 3. Controleer of de signature overeenkomt met je lijst
        if (targetSignatures.Contains(symbol.ToSignatureString()))
        {
            // Bewaar de originele declaratie voor de comment (zonder body)
            var originalDesc = method.WithBody(null).WithSemicolonToken(default).ToString().Trim();
            var commentTrivia = SyntaxFactory.Comment($" // Original: {originalDesc}");

            // 4. Maak de methode public
            // SetAccessibility verwijdert automatisch modifiers zoals 'private' of 'internal'
            editor.SetAccessibility(method, Accessibility.Public);

            // 5. Voeg de comment toe aan de parameterlijst (TrailingTrivia)
            var newParameterList = method.ParameterList.WithAppendedTrailingTrivia(commentTrivia);
            editor.ReplaceNode(method.ParameterList, newParameterList);
        }
    }

    // 6. Geef het bijgewerkte document terug
    return editor.GetChangedDocument();
}
*/
