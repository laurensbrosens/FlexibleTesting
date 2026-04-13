using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FlexibleTesting.Generators;

[Generator]
public class FlexibleTestingGenerator : IIncrementalGenerator
{
    private void CreateCodeInTarget(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(ctx =>
        {
            ctx.AddEmbeddedAttributeDefinition();
            ctx.AddSource($"GeneratorInstructionsAttribute.g.cs", FlexibleTestingGeneratorCode.GeneratorInstructionsAttributeCode);
            ctx.AddSource($"IGeneratorInstructions.g.cs", FlexibleTestingGeneratorCode.GeneratorInstructionsInterfaceCode);
            ctx.AddSource($"Overwrites.g.cs", FlexibleTestingGeneratorCode.OverwritesHelperCode);
        });
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        CreateCodeInTarget(context);

        var targetClasses = context.SyntaxProvider.ForAttributeWithMetadataName(
            fullyQualifiedMetadataName: "FlexibleTesting.GeneratorInstructionsAttribute",
            predicate: IsAttributeOnAClass(),
            transform: GetTargetClassesToGenerate
        );

        context.RegisterSourceOutput(
            targetClasses,
            static (ctx, targetClass) =>
            {
                GenerateCopyWithChanges(ctx, targetClass);
            }
        );
    }

    public static BuilderClassData GetTargetClassesToGenerate(GeneratorAttributeSyntaxContext context, CancellationToken ct)
    {
        var generatorInstructionsClass = (ClassDeclarationSyntax)context.TargetNode;
        var configureMethod = generatorInstructionsClass
            .Members.OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == "Configure");

        if (configureMethod?.Body == null)
        {
            return default;
        }

        var builderNameSpace = context.TargetSymbol.ContainingNamespace.ToDisplayString();
        var builderClass = generatorInstructionsClass.Identifier.Text;
        var builderData = new BuilderClassData(builderNameSpace, builderClass, default);
        var methodsToMakePublic = new List<IMethodSymbol>();
        ITypeSymbol targetTypeSymbol = null;

        var invocations = configureMethod.Body.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            var symbol = context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol as IMethodSymbol;
            if (symbol == null || symbol.ContainingType?.Name != nameof(Overwrites))
                continue;

            switch (symbol.Name)
            {
                case nameof(Overwrites.ForClass): // && symbol.IsGenericMethod?
                    targetTypeSymbol = AddForClass(symbol);
                    break;
                case nameof(Overwrites.MakePublic): // && invocation.ArgumentList.Arguments.Count > 0
                    AddToMakePublic(context, methodsToMakePublic, invocation, ct);
                    break;
                default:
                    break;
            }
        }

        if (targetTypeSymbol != null && targetTypeSymbol.TypeKind != TypeKind.Error)
        {
            var syntaxReference = targetTypeSymbol.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxReference == null)
                return default;

            var classNode = (ClassDeclarationSyntax)syntaxReference.GetSyntax(ct);
            var oldName = classNode.Identifier.Text;
            var newName = $"{oldName}_G";

            var root = classNode.SyntaxTree.GetRoot(ct);
            var targetSemanticModel = context.SemanticModel.Compilation.GetSemanticModel(classNode.SyntaxTree);
            var rewriter = new ClassRenamer(targetSemanticModel, oldName, newName, methodsToMakePublic);
            var newRoot = rewriter.Visit(root);

            return builderData with
            {
                TargetClass = new TargetClassData(targetTypeSymbol.ContainingNamespace.ToDisplayString(), oldName, newRoot.ToFullString()),
            };
        }

        return default;
    }

    private static ITypeSymbol AddForClass(IMethodSymbol symbol)
    {
        return symbol.TypeArguments.FirstOrDefault();
    }

    private static void AddToMakePublic(
        GeneratorAttributeSyntaxContext context,
        List<IMethodSymbol> methodsToMakePublic,
        InvocationExpressionSyntax invocation,
        CancellationToken ct
    )
    {
        var argument = invocation.ArgumentList.Arguments[0].Expression;

        // Check for "x => x.Method" and "() => Method"
        if (argument is not LambdaExpressionSyntax lambda)
        {
            throw new System.Exception("Invalid lambda expression"); // TODO: add diagnostics instead of exception.
        }

        // Find symbol in lambda. If something like "() => Method()", inspect the expression itself
        SyntaxNode nodeToInspect = lambda.Body is InvocationExpressionSyntax invocationBody ? invocationBody.Expression : lambda.Body;

        var methodSymbol = context.SemanticModel.GetSymbolInfo(nodeToInspect, ct).Symbol as IMethodSymbol;

        if (methodSymbol != null)
        {
            methodsToMakePublic.Add(methodSymbol);
        }
    }

    private static void GenerateCopyWithChanges(SourceProductionContext context, BuilderClassData targetData)
    {
        string source = $$"""
            // <auto-generated/>
            {{targetData.TargetClass.FullContent}}
            """;
        const string ClassNameSuffix = "_G";
        context.AddSource($"{targetData.TargetClass.ClassName}{ClassNameSuffix}.g.cs", source);
    }

    private static System.Func<SyntaxNode, CancellationToken, bool> IsAttributeOnAClass()
    {
        return static (s, _) => s is ClassDeclarationSyntax;
    }
}

public record struct TargetClassData(string Namespace, string ClassName, string FullContent);

/// <summary>
/// Immutable structure corresponding to the Builder with the instructions
/// </summary>
public record struct BuilderClassData(string Namespace, string ClassName, TargetClassData TargetClass);

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
