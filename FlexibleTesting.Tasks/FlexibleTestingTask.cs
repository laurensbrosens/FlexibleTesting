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

    // Legacy project sources (e.g. LegacyCodeProject)
    public ITaskItem[] LegacySourceFiles { get; set; } = Array.Empty<ITaskItem>();

    // Assembly name of legacy project (used to filter out its DLL from references for the legacy source compilation)
    public string LegacyAssemblyName { get; set; } = string.Empty;

    // Optional, but helps parsing when you use #if in code
    public string DefineConstants { get; set; } = string.Empty;

    private string TestProjectName =>
        string.IsNullOrWhiteSpace(ProjectFilePath) ? "<unknown>" : Path.GetFileNameWithoutExtension(ProjectFilePath);

    private string TestProjectDisplay =>
        string.IsNullOrWhiteSpace(ProjectFilePath) ? "<unknown project>" : $"{TestProjectName} ({ProjectFilePath})";

    private string? TestProjectDirectory => string.IsNullOrWhiteSpace(ProjectFilePath) ? null : Path.GetDirectoryName(ProjectFilePath);

    private string LegacyDisplay
    {
        get
        {
            var legacyName = string.IsNullOrWhiteSpace(LegacyAssemblyName) ? "<unknown legacy assembly>" : LegacyAssemblyName;

            var legacyRoot = TryGetCommonRootDirectory(
                LegacySourceFiles.Select(i => GetItemFullPath(i)).Where(p => !string.IsNullOrWhiteSpace(p))!
            );

            return legacyRoot != null
                ? $"{legacyName} (sources under {legacyRoot})"
                : $"{legacyName} (LegacySourceFiles={LegacySourceFiles.Length})";
        }
    }

    public override bool Execute()
    {
        try
        {
            OutputPath = Path.GetFullPath(OutputPath);

            Log.LogMessage(MessageImportance.High, $"FlexibleTestingTask running for {TestProjectDisplay}. OutputPath={OutputPath}");

            if (LegacySourceFiles.Length > 0)
            {
                Log.LogMessage(MessageImportance.High, $"Legacy sources provided: {LegacyDisplay}");
            }
            else
            {
                Log.LogMessage(
                    MessageImportance.Low,
                    $"No LegacySourceFiles provided. If you target types from '{LegacyAssemblyName}', DeclaringSyntaxReferences will not be available."
                );
            }

            var parseOptions = CreateParseOptions(DefineConstants);
            var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

            // References for the test project compilation (the project currently being built)
            var metadataReferencesForTestProject = CreateMetadataReferences(
                References,
                excludeAssemblyName: null,
                projectDisplayForLogging: TestProjectDisplay
            );

            // References for the legacy-source compilation (exclude Legacy assembly DLL itself to reduce type-duplication issues)
            var metadataReferencesForLegacySourceCompilation = CreateMetadataReferences(
                References,
                excludeAssemblyName: string.IsNullOrWhiteSpace(LegacyAssemblyName) ? null : LegacyAssemblyName,
                projectDisplayForLogging: $"legacy source compilation for {LegacyDisplay}"
            );

            var workspace = new AdhocWorkspace();

            // "Test project" Roslyn project (built from SourceFiles)
            var testProjectId = ProjectId.CreateNewId();
            var testProjectInfo = ProjectInfo.Create(
                testProjectId,
                VersionStamp.Create(),
                name: TestProjectName,
                assemblyName: TestProjectName,
                language: LanguageNames.CSharp,
                filePath: ProjectFilePath,
                compilationOptions: compilationOptions,
                parseOptions: parseOptions,
                metadataReferences: metadataReferencesForTestProject
            );

            var solution = workspace.CurrentSolution.AddProject(testProjectInfo);
            solution = AddDocuments(solution, testProjectId, SourceFiles, expectedRootDirectoryForWarning: TestProjectDirectory, Log);

            // Legacy source compilation as a second Roslyn project built from LegacySourceFiles
            Compilation? legacyCompilation = null;
            string? legacyProjectNameForLogging = null;

            if (LegacySourceFiles.Length > 0)
            {
                legacyProjectNameForLogging =
                    $"LegacySources_{(string.IsNullOrWhiteSpace(LegacyAssemblyName) ? "Unknown" : LegacyAssemblyName)}";

                var legacyProjectId = ProjectId.CreateNewId();
                var legacyProjectInfo = ProjectInfo.Create(
                    legacyProjectId,
                    VersionStamp.Create(),
                    name: legacyProjectNameForLogging,
                    assemblyName: legacyProjectNameForLogging,
                    language: LanguageNames.CSharp,
                    filePath: null,
                    compilationOptions: compilationOptions,
                    parseOptions: parseOptions,
                    metadataReferences: metadataReferencesForLegacySourceCompilation
                );

                solution = solution.AddProject(legacyProjectInfo);
                solution = AddDocuments(solution, legacyProjectId, LegacySourceFiles, expectedRootDirectoryForWarning: null, Log);

                var legacyProject = solution.GetProject(legacyProjectId);
                legacyCompilation = legacyProject?.GetCompilationAsync().GetAwaiter().GetResult();

                if (legacyCompilation == null)
                {
                    Log.LogError(
                        $"[{TestProjectDisplay}] Could not create compilation for legacy sources ({LegacyDisplay}). "
                            + $"DeclaringSyntaxReferences for types from '{LegacyAssemblyName}' will not work."
                    );
                }
                else
                {
                    Log.LogMessage(
                        MessageImportance.High,
                        $"[{TestProjectDisplay}] Legacy compilation created successfully. SyntaxTrees={legacyCompilation.SyntaxTrees.Count()}"
                    );
                }
            }

            var testProject = solution.GetProject(testProjectId);
            if (testProject == null)
            {
                Log.LogError($"[{TestProjectDisplay}] Could not get Roslyn project from AdhocWorkspace solution.");
                return false;
            }

            var testCompilation = testProject.GetCompilationAsync().GetAwaiter().GetResult();
            if (testCompilation == null)
            {

                Log.LogError($"[{TestProjectDisplay}] Could not get compilation.");
                return false;
            }

            Log.LogMessage(
                MessageImportance.High,
                $"[{TestProjectDisplay}] Test compilation created. SyntaxTrees={testCompilation.SyntaxTrees.Count()}"
            );

            // IMPORTANT:
            // The attribute is searched in the TEST PROJECT compilation (built from SourceFiles),
            // not the legacy compilation.
            var generatorInstructionsAttribute = testCompilation.GetTypeByMetadataName("FlexibleTesting.GeneratorInstructionsAttribute");

            if (generatorInstructionsAttribute == null)
            {
                Log.LogMessage(
                    $"[{TestProjectDisplay}] Could not find FlexibleTesting.GeneratorInstructionsAttribute in the test compilation. "
                        + $"This usually means FlexibleTesting is not referenced/resolved during this build (missing/invalid ReferencePath entries)."
                );
                return true; // No attribute found, so nothing to do, but not an error. Just log and exit successfully.
            }

            var attributedInstructionClassCount = 0;

            foreach (var syntaxTree in testCompilation.SyntaxTrees)
            {
                var semanticModel = testCompilation.GetSemanticModel(syntaxTree);
                var root = syntaxTree.GetRoot();
                var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

                foreach (var classNode in classes)
                {
                    var symbol = semanticModel.GetDeclaredSymbol(classNode);
                    if (symbol == null)
                        continue;

                    if (
                        symbol
                            .GetAttributes()
                            .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, generatorInstructionsAttribute))
                    )
                    {
                        attributedInstructionClassCount++;
                        GenerateForFlexibleTesting(
                            testCompilation,
                            legacyCompilation,
                            semanticModel,
                            classNode,
                            symbol,
                            legacyProjectNameForLogging
                        );
                    }
                }
            }

            Log.LogMessage(
                MessageImportance.High,
                $"[{TestProjectDisplay}] Found {attributedInstructionClassCount} instruction class(es) with [GeneratorInstructions]."
            );

            return !Log.HasLoggedErrors;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: true);
            return false;
        }
    }

    private void GenerateForFlexibleTesting(
        Compilation testCompilation,
        Compilation? legacyCompilation,
        SemanticModel semanticModelB,
        ClassDeclarationSyntax classNode,
        INamedTypeSymbol builderInstructionsSymbol,
        string? legacyProjectNameForLogging
    )
    {
        var configureMethod = classNode.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == "Configure");

        if (configureMethod?.Body == null)
        {
            Log.LogError(
                $"[{TestProjectDisplay}] Configure() method not found (or has no body) in instruction class '{builderInstructionsSymbol.ToDisplayString()}'."
            );
            return;
        }

        var methodsToMakePublicFromTest = new List<IMethodSymbol>();
        INamedTypeSymbol? targetTypeFromTest = null;

        var invocations = configureMethod.Body.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var invocation in invocations)
        {
            var methodSymbol = semanticModelB.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (methodSymbol == null || methodSymbol.ContainingType?.Name != "Overwrites")
                continue;

            switch (methodSymbol.Name)
            {
                case "ForClass":
                    targetTypeFromTest = methodSymbol.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
                    break;

                case "MakePublic":
                    AddToMakePublic(semanticModelB, methodsToMakePublicFromTest, invocation);
                    break;
            }
        }

        if (targetTypeFromTest == null || targetTypeFromTest.TypeKind == TypeKind.Error)
        {
            Log.LogError(
                $"[{TestProjectDisplay}] Overwrites.ForClass<TClass>() did not resolve a valid target type "
                    + $"in instruction class '{builderInstructionsSymbol.ToDisplayString()}'."
            );
            return;
        }

        if (legacyCompilation == null)
        {
            Log.LogError(
                $"[{TestProjectDisplay}] Legacy compilation not available. Pass LegacySourceFiles so we can access DeclaringSyntaxReferences for types from {LegacyDisplay}. "
                    + $"Target type requested: {targetTypeFromTest.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}"
            );
            return;
        }

        // Re-resolve the type in the legacy SOURCE compilation so it becomes a SOURCE symbol
        var targetMetadataName = GetTypeMetadataName(targetTypeFromTest.OriginalDefinition);
        var targetTypeInLegacy = legacyCompilation.GetTypeByMetadataName(targetMetadataName);

        if (targetTypeInLegacy == null)
        {
            Log.LogError(
                $"[{TestProjectDisplay}] Could not find target type '{targetMetadataName}' in legacy source compilation ({LegacyDisplay}). "
                    + $"(legacy project name in workspace: {legacyProjectNameForLogging ?? "<unknown>"})"
            );
            return;
        }

        var typeSyntaxRef = targetTypeInLegacy.DeclaringSyntaxReferences.FirstOrDefault();
        if (typeSyntaxRef == null)
        {
            Log.LogError(
                $"[{TestProjectDisplay}] Target type '{targetTypeInLegacy.ToDisplayString()}' was found in legacy compilation, "
                    + $"but still has no DeclaringSyntaxReferences. Ensure LegacySourceFiles includes the defining .cs file(s). "
                    + $"Legacy: {LegacyDisplay}"
            );
            return;
        }

        if (typeSyntaxRef.GetSyntax() is not ClassDeclarationSyntax targetClassNode)
        {
            Log.LogError(
                $"[{TestProjectDisplay}] Declaring syntax for '{targetTypeInLegacy.ToDisplayString()}' was not a class declaration."
            );
            return;
        }

        // IMPORTANT: symbols from testCompilation won't match symbols from legacyCompilation,
        // so map methods-to-make-public by signature.
        var methodsToMakePublicInLegacy = MapMethodsToLegacy(targetTypeInLegacy, methodsToMakePublicFromTest);

        var oldName = targetClassNode.Identifier.Text;
        var newName = $"{oldName}_G";

        var targetRoot = targetClassNode.SyntaxTree.GetRoot();
        var legacySemanticModel = legacyCompilation.GetSemanticModel(targetClassNode.SyntaxTree);

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
        Log.LogMessage(
            MessageImportance.High,
            $"[{TestProjectDisplay}] Generated {fullPath} from legacy type {targetTypeInLegacy.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}"
        );
    }

    private static List<IMethodSymbol> MapMethodsToLegacy(INamedTypeSymbol legacyType, List<IMethodSymbol> methodsFromTest)
    {
        var legacyMethods = legacyType.GetMembers().OfType<IMethodSymbol>().ToList();
        var result = new List<IMethodSymbol>();

        foreach (var mb in methodsFromTest)
        {
            var match = legacyMethods.FirstOrDefault(ml => MethodsMatch(ml, mb));
            if (match != null)
                result.Add(match);
        }

        return result;

        static bool MethodsMatch(IMethodSymbol legacyMethod, IMethodSymbol methodFromTest)
        {
            if (!string.Equals(legacyMethod.Name, methodFromTest.Name, StringComparison.Ordinal))
                return false;

            if (legacyMethod.Parameters.Length != methodFromTest.Parameters.Length)
                return false;

            for (int i = 0; i < legacyMethod.Parameters.Length; i++)
            {
                var a = legacyMethod.Parameters[i].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var b = methodFromTest.Parameters[i].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (!string.Equals(a, b, StringComparison.Ordinal))
                    return false;
            }

            return true;
        }
    }

    private static Solution AddDocuments(
        Solution solution,
        ProjectId projectId,
        ITaskItem[] items,
        string? expectedRootDirectoryForWarning,
        TaskLoggingHelper log
    )
    {
        foreach (var item in items)
        {
            var filePath = GetItemFullPath(item);
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                continue;

            if (!string.IsNullOrWhiteSpace(expectedRootDirectoryForWarning))
            {
                var full = Path.GetFullPath(filePath);
                var root = Path.GetFullPath(expectedRootDirectoryForWarning);

                // Not an error, but a very useful signal if you suspect "wrong project files are being compiled".
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    log.LogMessage(
                        MessageImportance.Low,
                        $"[FlexibleTestingTask] Note: source file is outside the test project directory: {full}"
                    );
                }
            }

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

    private List<MetadataReference> CreateMetadataReferences(
        ITaskItem[] references,
        string? excludeAssemblyName,
        string projectDisplayForLogging
    )
    {
        var list = new List<MetadataReference>();

        foreach (var reference in references)
        {
            var filePath = reference.GetMetadata("FullPath");
            if (string.IsNullOrWhiteSpace(filePath))
                filePath = reference.ItemSpec;

            if (string.IsNullOrWhiteSpace(filePath))
                continue;

            if (!File.Exists(filePath))
            {
                // Previously you silently skipped these. Logging helps explain missing attributes/types.
                Log.LogMessage(MessageImportance.Low, $"[{projectDisplayForLogging}] Reference path does not exist (skipped): {filePath}");
                continue;
            }

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

        var ns = symbol.ContainingNamespace is { IsGlobalNamespace: false } ? symbol.ContainingNamespace.ToDisplayString() : null;

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

    private static string? TryGetCommonRootDirectory(IEnumerable<string> filePaths)
    {
        var paths = filePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.GetFullPath(p)).ToList();

        if (paths.Count == 0)
            return null;

        var dirParts = paths
            .Select(p => (Path.GetDirectoryName(p) ?? "").Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Where(parts => parts.Length > 0)
            .ToList();

        if (dirParts.Count == 0)
            return null;

        int minLen = dirParts.Min(a => a.Length);
        int commonLen = 0;

        for (int i = 0; i < minLen; i++)
        {
            var candidate = dirParts[0][i];
            if (dirParts.All(a => string.Equals(a[i], candidate, StringComparison.OrdinalIgnoreCase)))
                commonLen++;
            else
                break;
        }

        if (commonLen == 0)
            return null;

        var commonParts = dirParts[0].Take(commonLen);
        var root = string.Join(Path.DirectorySeparatorChar.ToString(), commonParts);

        // Ensure it's an absolute-ish root if possible
        if (Path.IsPathRooted(paths[0]))
        {
            // Rebuild from drive root + parts, easiest is just return DirectoryName of a file truncated:
            var firstDir = Path.GetDirectoryName(paths[0]) ?? "";
            var rootFull = firstDir;

            // Trim until it matches the computed root suffix
            // (simple approach: walk up until it ends with root string)
            while (!string.IsNullOrEmpty(rootFull) && !rootFull.EndsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                rootFull = Path.GetDirectoryName(rootFull) ?? "";
            }

            return string.IsNullOrEmpty(rootFull) ? null : rootFull;
        }

        return root;
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
                methodsToMakePublic.Add(methodSymbol);
        }
    }
}
