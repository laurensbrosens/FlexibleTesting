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

    [Required]
    public ITaskItem[] SourceFiles { get; set; } = Array.Empty<ITaskItem>();

    [Required]
    public ITaskItem[] References { get; set; } = Array.Empty<ITaskItem>();

    // Project A sources (LegacyCodeProject)
    public ITaskItem[] LegacySourceFiles { get; set; } = Array.Empty<ITaskItem>();

    // Assembly name of Project A, e.g. "LegacyCodeProject" (used to filter out its DLL from references)
    public string LegacyAssemblyName { get; set; } = string.Empty;

    // Optional, but helps parsing when you use #if in code
    public string DefineConstants { get; set; } = string.Empty;

    public override bool Execute()
    {
        try
        {
            OutputPath = Path.GetFullPath(OutputPath);

            var parseOptions = CreateParseOptions(DefineConstants);
            var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

            var metadataReferencesForB = CreateMetadataReferences(References, excludeAssemblyName: null);
            var metadataReferencesForLegacy = CreateMetadataReferences(
                References,
                excludeAssemblyName: string.IsNullOrWhiteSpace(LegacyAssemblyName) ? null : LegacyAssemblyName
            );

            var workspace = new AdhocWorkspace();

            // Project B (the project being built)
            var projectBId = ProjectId.CreateNewId();
            var projectBInfo = ProjectInfo.Create(
                projectBId,
                VersionStamp.Create(),
                name: Path.GetFileNameWithoutExtension(ProjectFilePath),
                assemblyName: Path.GetFileNameWithoutExtension(ProjectFilePath),
                language: LanguageNames.CSharp,
                filePath: ProjectFilePath,
                compilationOptions: compilationOptions,
                parseOptions: parseOptions,
                metadataReferences: metadataReferencesForB
            );

            var solution = workspace.CurrentSolution.AddProject(projectBInfo);
            solution = AddDocuments(solution, projectBId, SourceFiles);

            // Project A (Legacy) as a second Roslyn project built from source files
            Compilation? legacyCompilation = null;
            if (LegacySourceFiles.Length > 0)
            {
                var legacyProjectId = ProjectId.CreateNewId();

                var legacyProjectInfo = ProjectInfo.Create(
                    legacyProjectId,
                    VersionStamp.Create(),
                    name: "LegacyCodeProject_Source",
                    assemblyName: "LegacyCodeProject_Source",
                    language: LanguageNames.CSharp,
                    filePath: null,
                    compilationOptions: compilationOptions,
                    parseOptions: parseOptions,
                    metadataReferences: metadataReferencesForLegacy
                );

                solution = solution.AddProject(legacyProjectInfo);
                solution = AddDocuments(solution, legacyProjectId, LegacySourceFiles);

                var legacyProject = solution.GetProject(legacyProjectId);
                legacyCompilation = legacyProject?.GetCompilationAsync().GetAwaiter().GetResult();
                if (legacyCompilation == null)
                {
                    Log.LogError("Could not create compilation for LegacySourceFiles. DeclaringSyntaxReferences for Project A types will not work.");
                }
            }
            else
            {
                Log.LogMessage(MessageImportance.Low, "LegacySourceFiles not provided; cannot resolve Project A types to source.");
            }

            var projectB = solution.GetProject(projectBId);
            if (projectB == null)
            {
                Log.LogError("Could not get Project B from AdhocWorkspace solution.");
                return false;
            }

            var compilationB = projectB.GetCompilationAsync().GetAwaiter().GetResult();
            if (compilationB == null)
            {
                Log.LogError("Could not get compilation for Project B.");
                return false;
            }

            var generatorInstructionsAttribute =
                compilationB.GetTypeByMetadataName("FlexibleTesting.GeneratorInstructionsAttribute");

            if (generatorInstructionsAttribute == null)
            {
                Log.LogError("Could not find FlexibleTesting.GeneratorInstructionsAttribute in Project B compilation.");
                return false;
            }

            foreach (var syntaxTree in compilationB.SyntaxTrees)
            {
                var semanticModel = compilationB.GetSemanticModel(syntaxTree);
                var root = syntaxTree.GetRoot();
                var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

                foreach (var classNode in classes)
                {
                    var symbol = semanticModel.GetDeclaredSymbol(classNode);
                    if (symbol == null)
                        continue;

                    if (symbol.GetAttributes().Any(a =>
                            SymbolEqualityComparer.Default.Equals(a.AttributeClass, generatorInstructionsAttribute)))
                    {
                        GenerateForFlexibleTesting(compilationB, legacyCompilation, semanticModel, classNode, symbol);
                    }
                }
            }

            return !Log.HasLoggedErrors;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: true);
            return false;
        }
    }

    private void GenerateForFlexibleTesting(
        Compilation compilationB,
        Compilation? compilationLegacy,
        SemanticModel semanticModelB,
        ClassDeclarationSyntax classNode,
        INamedTypeSymbol builderInstructionsSymbol)
    {
        var configureMethod = classNode.Members
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == "Configure");

        if (configureMethod?.Body == null)
        {
            Log.LogError($"Configure() method not found (or has no body) in '{builderInstructionsSymbol.ToDisplayString()}'.");
            return;
        }

        var methodsToMakePublicFromB = new List<IMethodSymbol>();
        INamedTypeSymbol? targetTypeFromB = null;

        var invocations = configureMethod.Body.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var invocation in invocations)
        {
            var methodSymbol = semanticModelB.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (methodSymbol == null || methodSymbol.ContainingType?.Name != "Overwrites")
                continue;

            switch (methodSymbol.Name)
            {
                case "ForClass":
                    targetTypeFromB = methodSymbol.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
                    break;

                case "MakePublic":
                    AddToMakePublic(semanticModelB, methodsToMakePublicFromB, invocation);
                    break;
            }
        }

        if (targetTypeFromB == null || targetTypeFromB.TypeKind == TypeKind.Error)
        {
            Log.LogError("Overwrites.ForClass<TClass>() did not resolve a valid target type.");
            return;
        }

        if (compilationLegacy == null)
        {
            Log.LogError("Legacy compilation not available. Pass LegacySourceFiles so we can access DeclaringSyntaxReferences for Project A types.");
            return;
        }

        // Re-resolve the type in the "legacy source compilation" so it becomes a SOURCE symbol
        var targetMetadataName = GetTypeMetadataName(targetTypeFromB.OriginalDefinition);
        var targetTypeInLegacy = compilationLegacy.GetTypeByMetadataName(targetMetadataName);

        if (targetTypeInLegacy == null)
        {
            Log.LogError($"Could not find '{targetMetadataName}' in LegacySourceFiles compilation.");
            return;
        }

        var typeSyntaxRef = targetTypeInLegacy.DeclaringSyntaxReferences.FirstOrDefault();
        if (typeSyntaxRef == null)
        {
            Log.LogError($"Type '{targetTypeInLegacy.ToDisplayString()}' still has no DeclaringSyntaxReferences. Check that LegacySourceFiles contains the defining .cs file(s).");
            return;
        }

        if (typeSyntaxRef.GetSyntax() is not ClassDeclarationSyntax targetClassNode)
        {
            Log.LogError($"Declaring syntax for '{targetTypeInLegacy.ToDisplayString()}' was not a class declaration.");
            return;
        }

        // IMPORTANT: symbols from compilationB won't match symbols from compilationLegacy,
        // so map methods-to-make-public by signature.
        var methodsToMakePublicInLegacy = MapMethodsToLegacy(targetTypeInLegacy, methodsToMakePublicFromB);

        var oldName = targetClassNode.Identifier.Text;
        var newName = $"{oldName}_G";

        var targetRoot = targetClassNode.SyntaxTree.GetRoot();
        var legacySemanticModel = compilationLegacy.GetSemanticModel(targetClassNode.SyntaxTree);

        var rewriter = new ClassRenamer(legacySemanticModel, oldName, newName, methodsToMakePublicInLegacy);
        var newRoot = rewriter.Visit(targetRoot);

        var result = $"""
// <auto-generated/>
{newRoot.ToFullString()}
""";

        Directory.CreateDirectory(OutputPath);
        var fileName = $"{oldName}_G.g.cs";
        var fullPath = Path.Combine(OutputPath, fileName);

        File.WriteAllText(fullPath, result, Encoding.UTF8);
        Log.LogMessage(MessageImportance.High, $"Generated {fullPath}");
    }

    private static List<IMethodSymbol> MapMethodsToLegacy(INamedTypeSymbol legacyType, List<IMethodSymbol> methodsFromB)
    {
        var legacyMethods = legacyType.GetMembers().OfType<IMethodSymbol>().ToList();
        var result = new List<IMethodSymbol>();

        foreach (var mb in methodsFromB)
        {
            var match = legacyMethods.FirstOrDefault(ml => MethodsMatch(ml, mb));
            if (match != null)
                result.Add(match);
        }

        return result;

        static bool MethodsMatch(IMethodSymbol legacyMethod, IMethodSymbol methodFromB)
        {
            if (!string.Equals(legacyMethod.Name, methodFromB.Name, StringComparison.Ordinal))
                return false;

            if (legacyMethod.Parameters.Length != methodFromB.Parameters.Length)
                return false;

            for (int i = 0; i < legacyMethod.Parameters.Length; i++)
            {
                var a = legacyMethod.Parameters[i].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var b = methodFromB.Parameters[i].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (!string.Equals(a, b, StringComparison.Ordinal))
                    return false;
            }

            return true;
        }
    }

    private static Solution AddDocuments(Solution solution, ProjectId projectId, ITaskItem[] items)
    {
        foreach (var item in items)
        {
            var filePath = GetItemFullPath(item);
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                continue;

            var documentId = DocumentId.CreateNewId(projectId);
            var documentInfo = DocumentInfo.Create(
                documentId,
                name: Path.GetFileName(filePath),
                loader: TextLoader.From(
                    TextAndVersion.Create(SourceText.From(File.ReadAllText(filePath), Encoding.UTF8), VersionStamp.Create())
                ),
                filePath: filePath
            );

            solution = solution.AddDocument(documentInfo);
        }

        return solution;
    }

    private List<MetadataReference> CreateMetadataReferences(ITaskItem[] references, string? excludeAssemblyName)
    {
        var list = new List<MetadataReference>();

        foreach (var reference in references)
        {
            var filePath = reference.GetMetadata("FullPath");
            if (string.IsNullOrWhiteSpace(filePath))
                filePath = reference.ItemSpec;

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                continue;

            if (!string.IsNullOrWhiteSpace(excludeAssemblyName))
            {
                var name = Path.GetFileNameWithoutExtension(filePath);
                if (string.Equals(name, excludeAssemblyName, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            list.Add(MetadataReference.CreateFromFile(filePath));
        }

        return list;
    }

    private static CSharpParseOptions CreateParseOptions(string defineConstants)
    {
        var symbols = new List<string>();

        if (!string.IsNullOrWhiteSpace(defineConstants))
        {
            // MSBuild DefineConstants usually looks like: "DEBUG;TRACE;SOMETHING"
            symbols.AddRange(
                defineConstants
                    .Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
            );
        }

        return new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: symbols);
    }

    // Namespace.Outer+Inner`1
    private static string GetTypeMetadataName(INamedTypeSymbol symbol)
    {
        symbol = symbol.OriginalDefinition;

        var ns = symbol.ContainingNamespace is { IsGlobalNamespace: false }
            ? symbol.ContainingNamespace.ToDisplayString()
            : null;

        var typeParts = new Stack<string>();
        for (INamedTypeSymbol? t = symbol; t != null; t = t.ContainingType)
            typeParts.Push(t.MetadataName);

        var typeName = string.Join("+", typeParts);
        return string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";
    }

    private static string GetItemFullPath(ITaskItem item)
    {
        var p = item.GetMetadata("FullPath");
        if (!string.IsNullOrWhiteSpace(p))
            return p;

        return item.ItemSpec;
    }

    private void AddToMakePublic(
        SemanticModel semanticModel,
        List<IMethodSymbol> methodsToMakePublic,
        InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList.Arguments.Count == 0)
            return;

        var argument = invocation.ArgumentList.Arguments[0].Expression;

        if (argument is LambdaExpressionSyntax lambda)
        {
            SyntaxNode nodeToInspect =
                lambda.Body is InvocationExpressionSyntax invocationBody ? invocationBody.Expression : lambda.Body;

            var methodSymbol = semanticModel.GetSymbolInfo(nodeToInspect).Symbol as IMethodSymbol;
            if (methodSymbol != null)
                methodsToMakePublic.Add(methodSymbol);
        }
    }
}