using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

        // Find the "Configure" method inside this class
        var configureMethod = generatorInstructionsClass.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == "Configure");

        if (configureMethod == null || configureMethod.Body == null)
        {
            return default;
        }

        // Look through all method calls (InvocationExpressions) inside the Configure method body
        var methodCalls = configureMethod.Body.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(i => i.Expression).OfType<MemberAccessExpressionSyntax>();

        foreach (var methodCall in methodCalls) // Looks for MemberAccessExpression (e.g., Object.Method)
        {
            // Check if the object being called is "Overwrites"
            if (methodCall.Expression.ToString() != "Overwrites" || methodCall.Name is not GenericNameSyntax genericName || genericName.Identifier.Text != "ForClass")
            {
                continue;
            }

            // Get the <T> part (e.g., UserViewModel)
            var typeArgument = genericName.TypeArgumentList.Arguments.FirstOrDefault();
            if (typeArgument == null)
            {
                return default;
            }

            // Now we use the SemanticModel.
            // Syntax just tells us it says "UserViewModel".
            // SemanticModel tells us EXACTLY what UserViewModel is (its namespace, etc.)
            var typeInfo = context.SemanticModel.GetTypeInfo(typeArgument, ct);

            // If the compiler successfully figured out what type T is...
            if (typeInfo.Type != null && typeInfo.Type.TypeKind != TypeKind.Error)
            {
                var typeSymbol = typeInfo.Type;

                // Extract just the strings we need for code generation!
                var namespaceName = typeSymbol.ContainingNamespace.IsGlobalNamespace ? string.Empty : typeSymbol.ContainingNamespace.ToDisplayString();

                var className = typeSymbol.Name;

                var syntaxReference = typeSymbol.DeclaringSyntaxReferences.FirstOrDefault();
                if (syntaxReference == null)
                {
                    throw new System.Exception($"Oh oh");
                    return default; // TODO: Add diagnostic that the target class must be in the same project
                }
                var syntaxNode = syntaxReference.GetSyntax(ct);
                string fullClassContent = syntaxNode.ToFullString();
                fullClassContent+= syntaxNode.SyntaxTree.GetCompilationUnitRoot();

                var classNode = (ClassDeclarationSyntax)syntaxNode;
                var oldName = classNode.Identifier.Text;
                var newName = $"{oldName}_G";
                var root = syntaxNode.SyntaxTree.GetRoot();
                var rewriter = new ClassRenamer(oldName, newName);
                var newRoot = rewriter.Visit(root);
                var betterAllContent = newRoot.ToFullString();
                return new TargetClassData(namespaceName, className, betterAllContent);
            }
        }
        return default; // TODO: Add diagnostic that Overwrites.ForClass<T>() is required
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
    private readonly string _oldName;
    private readonly string _newName;

    public ClassRenamer(string oldName, string newName)
    {
        _oldName = oldName;
        _newName = newName;
    }

    // Herstel de klassenaam zelf
    public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        if (node.Identifier.Text == _oldName)
        {
            node = node.WithIdentifier(SyntaxFactory.Identifier(_newName));
        }
        return base.VisitClassDeclaration(node);
    }

    // Herstel alle constructors
    public override SyntaxNode VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        if (node.Identifier.Text == _oldName)
        {
            node = node.WithIdentifier(SyntaxFactory.Identifier(_newName));
        }
        return base.VisitConstructorDeclaration(node);
    }

    // Optioneel: Herstel ook statische methoden of velden die de oude naam als type gebruiken
    public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
    {
        if (node.Identifier.Text == _oldName)
        {
            return node.WithIdentifier(SyntaxFactory.Identifier(_newName));
        }
        return base.VisitIdentifierName(node);
    }
}