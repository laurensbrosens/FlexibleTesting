using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FlexibleTesting.Tasks;

public class FlexibleTestingTask : Task
{
    [Required]
    public string ProjectFilePath { get; set; } = string.Empty;

    [Required]
    public string OutputPath { get; set; } = string.Empty;

    //[Required]
    public ITaskItem[] SourceFiles { get; set; } = Array.Empty<ITaskItem>();

    //[Required]
    public ITaskItem[] References { get; set; } = Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        try
        {
            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var metadataReferences = new List<MetadataReference>();
            foreach (var reference in References)
            {
                var filePath = reference.GetMetadata("FullPath");
                if (File.Exists(filePath))
                {
                    metadataReferences.Add(MetadataReference.CreateFromFile(filePath));
                }
            }

            var projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                Path.GetFileNameWithoutExtension(ProjectFilePath),
                Path.GetFileNameWithoutExtension(ProjectFilePath),
                LanguageNames.CSharp,
                filePath: ProjectFilePath,
                metadataReferences: metadataReferences
            );

            var solution = workspace.CurrentSolution.AddProject(projectInfo);

            foreach (var sourceFile in SourceFiles)
            {
                var filePath = sourceFile.GetMetadata("FullPath");
                if (File.Exists(filePath))
                {
                    var documentId = DocumentId.CreateNewId(projectId);
                    var documentInfo = DocumentInfo.Create(
                        documentId,
                        Path.GetFileName(filePath),
                        loader: TextLoader.From(
                            TextAndVersion.Create(SourceText.From(File.ReadAllText(filePath), Encoding.UTF8), VersionStamp.Create())
                        ),
                        filePath: filePath
                    );
                    solution = solution.AddDocument(documentInfo);
                }
            }

            var project = solution.GetProject(projectId);
            if (project == null)
            {
                Log.LogError("Could not get project from solution.");
                return false;
            }

            var compilation = project.GetCompilationAsync().GetAwaiter().GetResult();

            if (compilation == null)
            {
                Log.LogError("Could not get compilation for project.");
                return false;
            }

            var generatorInstructionsAttribute = compilation.GetTypeByMetadataName("FlexibleTesting.GeneratorInstructionsAttribute");
            var autoImplementAttribute = compilation.GetTypeByMetadataName("FlexibleTesting.AutoImplementPropertiesAttribute");

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = syntaxTree.GetRoot();
                var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

                foreach (var classNode in classes)
                {
                    var symbol = semanticModel.GetDeclaredSymbol(classNode);
                    if (symbol == null)
                        continue;

                    if (
                        generatorInstructionsAttribute != null
                        && symbol
                            .GetAttributes()
                            .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, generatorInstructionsAttribute))
                    )
                    {
                        GenerateForFlexibleTesting(compilation, semanticModel, classNode, symbol);
                    }

                    if (autoImplementAttribute != null)
                    {
                        var attr = symbol
                            .GetAttributes()
                            .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, autoImplementAttribute));
                        if (attr != null)
                        {
                            GenerateForAutoImplement(classNode, symbol, attr);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex);
            return false;
        }

        return !Log.HasLoggedErrors;
    }

    private void GenerateForFlexibleTesting(
        Compilation compilation,
        SemanticModel semanticModel,
        ClassDeclarationSyntax classNode,
        INamedTypeSymbol symbol
    )
    {
        var configureMethod = classNode.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == "Configure");
        if (configureMethod?.Body == null)
            return;

        var methodsToMakePublic = new List<IMethodSymbol>();
        ITypeSymbol? targetTypeSymbol = null;

        var invocations = configureMethod.Body.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            var methodSymbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (methodSymbol == null || methodSymbol.ContainingType?.Name != "Overwrites")
                continue;

            switch (methodSymbol.Name)
            {
                case "ForClass":
                    targetTypeSymbol = methodSymbol.TypeArguments.FirstOrDefault();
                    break;
                case "MakePublic":
                    AddToMakePublic(semanticModel, methodsToMakePublic, invocation);
                    break;
            }
        }

        if (targetTypeSymbol != null && targetTypeSymbol.TypeKind != TypeKind.Error)
        {
            var syntaxReference = targetTypeSymbol.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxReference == null)
                return;

            var targetClassNode = (ClassDeclarationSyntax)syntaxReference.GetSyntax();
            var oldName = targetClassNode.Identifier.Text;
            var newName = $"{oldName}_G";

            var targetRoot = targetClassNode.SyntaxTree.GetRoot();
            var targetSemanticModel = compilation.GetSemanticModel(targetClassNode.SyntaxTree);
            var rewriter = new ClassRenamer(targetSemanticModel, oldName, newName, methodsToMakePublic);
            var newRoot = rewriter.Visit(targetRoot);

            var result = $"""
// <auto-generated/>
{newRoot.ToFullString()}
""";
            var fileName = $"{oldName}_G.g.cs";
            var fullPath = Path.Combine(OutputPath, fileName);

            Directory.CreateDirectory(OutputPath);
            File.WriteAllText(fullPath, result);
            Log.LogMessage(MessageImportance.High, $"Generated {fullPath}");
        }
    }

    private void GenerateForAutoImplement(ClassDeclarationSyntax classNode, INamedTypeSymbol classSymbol, AttributeData attribute)
    {
        if (attribute.ConstructorArguments.Length == 0)
            return;

        foreach (TypedConstant constructorArgumentValue in attribute.ConstructorArguments[0].Values)
        {
            if (constructorArgumentValue.Value is INamedTypeSymbol { TypeKind: TypeKind.Interface } interfaceSymbol)
            {
                EquatableList<string> properties = new();

                foreach (IPropertySymbol interfaceProperty in interfaceSymbol.GetMembers().OfType<IPropertySymbol>())
                {
                    string type = interfaceProperty.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    string setter = interfaceProperty.SetMethod is not null ? "set; " : string.Empty;

                    properties.Add(
                        $$"""
                        public {{type}} {{interfaceProperty.Name}} { get; {{setter}}}
                        """
                    );
                }

                StringBuilder sourceBuilder = new(
                    $$"""
                    // <auto-generated/>
                    namespace {{classSymbol.ContainingNamespace.ToDisplayString()}};

                    public partial class {{classSymbol.Name}} : {{interfaceSymbol.ToDisplayString(
                        SymbolDisplayFormat.FullyQualifiedFormat
                    )}}
                    {
                    
                    """
                );

                foreach (string property in properties)
                {
                    sourceBuilder.AppendLine($"    {property}");
                }

                sourceBuilder.AppendLine("}");

                string fileName = $"{classSymbol.Name}_{interfaceSymbol.Name}.g.cs";
                string fullPath = Path.Combine(OutputPath, fileName);

                Directory.CreateDirectory(OutputPath);
                File.WriteAllText(fullPath, sourceBuilder.ToString());
                Log.LogMessage(MessageImportance.High, $"Generated {fullPath}");
            }
        }
    }

    private void AddToMakePublic(
        SemanticModel semanticModel,
        List<IMethodSymbol> methodsToMakePublic,
        InvocationExpressionSyntax invocation
    )
    {
        if (invocation.ArgumentList.Arguments.Count == 0)
            return;
        var argument = invocation.ArgumentList.Arguments[0].Expression;

        if (argument is LambdaExpressionSyntax lambda)
        {
            SyntaxNode nodeToInspect = lambda.Body is InvocationExpressionSyntax invocationBody ? invocationBody.Expression : lambda.Body;
            var methodSymbol = semanticModel.GetSymbolInfo(nodeToInspect).Symbol as IMethodSymbol;
            if (methodSymbol != null)
            {
                methodsToMakePublic.Add(methodSymbol);
            }
        }
    }
}
