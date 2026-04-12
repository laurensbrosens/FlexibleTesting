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

        // 1. Find classes with your specific attribute
        var targetClasses = context.SyntaxProvider.ForAttributeWithMetadataName(
            fullyQualifiedMetadataName: "FlexibleTesting.GeneratorInstructionsAttribute",
            predicate: IsAttributeOnAClass(),
            transform: GetTargetClassesToGenerate
        );

        context.RegisterSourceOutput(
            targetClasses,
            static (ctx, targetClass) =>
            {
                GeneratePartialClass(ctx, targetClass);
            }
        );
    }

    private static System.Func<SyntaxNode, CancellationToken, bool> IsAttributeOnAClass()
    {
        return static (s, _) => s is ClassDeclarationSyntax;
    }

    public static TargetClassData GetTargetClassesToGenerate(GeneratorAttributeSyntaxContext context, CancellationToken ct)
    {
        var generatorInstructionsClass = (ClassDeclarationSyntax)context.TargetNode;
        var configureMethod = generatorInstructionsClass.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == "Configure");

        if (configureMethod?.Body == null)
            return default;

        // We verzamelen hier de methoden die we public moeten maken
        var methodsToMakePublic = new List<IMethodSymbol>();
        ITypeSymbol targetTypeSymbol = null;

        var invocations = configureMethod.Body.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            var symbol = context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol as IMethodSymbol;
            if (symbol == null || symbol.ContainingType?.Name != "Overwrites")
                continue;

            // 1. Zoek naar ForClass<T>
            if (symbol.Name == "ForClass" && symbol.IsGenericMethod)
            {
                targetTypeSymbol = symbol.TypeArguments.FirstOrDefault();
            }

            // 2. Zoek naar MakePublic<TInterface, TDelegate>(x => x.Method)
            if (symbol.Name == "MakePublic" && invocation.ArgumentList.Arguments.Count > 0)
            {
                var argument = invocation.ArgumentList.Arguments[0].Expression;

                // Haal de lambda op (zowel x => x.Method als () => Method)
                if (argument is LambdaExpressionSyntax lambda)
                {
                    // We proberen het symbool te vinden van de body van de lambda
                    // Dit werkt voor: x => x.Method, () => Method, en () => Method()
                    SyntaxNode nodeToInspect = lambda.Body;

                    // Als de body een aanroep is, bijv. () => Method(), inspecteer dan de methode zelf
                    if (nodeToInspect is InvocationExpressionSyntax invocationBody)
                    {
                        nodeToInspect = invocationBody.Expression;
                    }

                    var methodSymbol = context.SemanticModel.GetSymbolInfo(nodeToInspect, ct).Symbol as IMethodSymbol;

                    if (methodSymbol != null)
                    {
                        methodsToMakePublic.Add(methodSymbol);
                    }
                }
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
            // Gebruik de uitgebreide rewriter
            var targetSemanticModel = context.SemanticModel.Compilation.GetSemanticModel(classNode.SyntaxTree);
            var rewriter = new ClassRenamer(targetSemanticModel, oldName, newName, methodsToMakePublic);
            var newRoot = rewriter.Visit(root);

            return new TargetClassData(targetTypeSymbol.ContainingNamespace.ToDisplayString(), oldName, newRoot.ToFullString());
        }

        return default;
    }

    private static void GeneratePartialClass(SourceProductionContext context, TargetClassData targetData)
    {
        // Handle global namespaces gracefully
        string namespaceDeclaration = string.IsNullOrEmpty(targetData.Namespace) ? "" : $"namespace {targetData.Namespace};";

        // Create the source code
        // Note: We use "partial" here so it merges with the original UserViewModel
        string source = $$"""
            // <auto-generated/>
            {{targetData.FullContent}}
            """;

        // Add the source file to the compilation
        // We use the class name as the filename (e.g., UserViewModel_Generated.g.cs)
        context.AddSource($"{targetData.ClassName}_G.g.cs", source);
    }
}

public record struct TargetClassData(string Namespace, string ClassName, string FullContent);

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
            var otherModifiers = node.Modifiers.Where(m => !m.IsKind(SyntaxKind.PrivateKeyword) && !m.IsKind(SyntaxKind.ProtectedKeyword)).ToList();

            var publicToken = SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithLeadingTrivia(leadingTrivia).WithTrailingTrivia(SyntaxFactory.Space);

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

public static class MethodSymbolExtensions
{
    public static string ToSignatureString(this IMethodSymbol symbol)
    {
        if (symbol == null)
            return string.Empty;

        // We gebruiken een format die alleen naar de 'inhoud' van de methode kijkt
        var format = new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
        );

        // Resultaat: "MethodName<T>(ParamType1, ParamType2)"
        return symbol.ToDisplayString(format);
    }
}
