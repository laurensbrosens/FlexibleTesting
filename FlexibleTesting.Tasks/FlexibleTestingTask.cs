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
        var mockablesFromTest = new List<MockableSpec>();
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

                case "Mockable":
                    AddToMockable(semanticModelB, mockablesFromTest, invocation);
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

        // Apply Mockable() rewrite(s) + auto dependency injection (IAutoDependencies) if needed
        if (mockablesFromTest.Count > 0 && newRoot is not null)
        {
            var depRewriter = new MockableAndDependenciesRewriter(
                targetClassName: newName,
                targetTypeFullName: targetTypeInLegacy.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                mockables: mockablesFromTest,
                dependenciesInterfaceName: "IAutoDependencies",
                dependenciesFieldName: "_dependencies",
                dependenciesParameterName: "dependencies"
            );

            newRoot = depRewriter.Visit(newRoot);
        }

        var normalized = newRoot!
            .NormalizeWhitespace(elasticTrivia: true)
            .ToFullString();

        var result = $"""
// <auto-generated/>
{normalized}
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

    private void AddToMockable(
        SemanticModel semanticModel,
        List<MockableSpec> mockables,
        InvocationExpressionSyntax invocation
    )
    {
        if (invocation.ArgumentList.Arguments.Count == 0)
            return;

        var argument = invocation.ArgumentList.Arguments[0].Expression;
        if (argument is not LambdaExpressionSyntax lambda)
            return;

        // Only support parameterless lambdas for now: () => Some.StaticMember or () => Some.StaticMethod(...)
        if (lambda switch
        {
            ParenthesizedLambdaExpressionSyntax pl => pl.ParameterList.Parameters.Count,
            SimpleLambdaExpressionSyntax => 1,
            _ => 0
        } != 0)
        {
            Log.LogError($"[{TestProjectDisplay}] Overwrites.Mockable only supports parameterless lambdas: Overwrites.Mockable(() => ...)");
            return;
        }

        ExpressionSyntax? bodyExpr = lambda.Body as ExpressionSyntax;
        if (bodyExpr == null && lambda.Body is BlockSyntax block)
        {
            // Support: () => { return DateTime.Now; }
            bodyExpr = block.Statements.OfType<ReturnStatementSyntax>().Select(r => r.Expression).FirstOrDefault(e => e != null) as ExpressionSyntax;
        }

        if (bodyExpr == null)
        {
            Log.LogError($"[{TestProjectDisplay}] Overwrites.Mockable lambda body must be an expression (or a block with a return expression).");
            return;
        }

        // Find the symbol referenced by the expression
        var symbol = semanticModel.GetSymbolInfo(bodyExpr).Symbol;

        // If body is member-access, sometimes symbol info binds to the "name" node; try that too.
        if (symbol == null && bodyExpr is MemberAccessExpressionSyntax mae)
            symbol = semanticModel.GetSymbolInfo(mae.Name).Symbol;

        // If body is invocation, bind the invoked expression too.
        if (symbol == null && bodyExpr is InvocationExpressionSyntax inv)
            symbol = semanticModel.GetSymbolInfo(inv.Expression).Symbol;

        if (symbol is not (IPropertySymbol or IFieldSymbol or IMethodSymbol))
        {
            Log.LogError(
                $"[{TestProjectDisplay}] Overwrites.Mockable could not resolve a property/field/method symbol from: {bodyExpr.WithoutTrivia().ToString()}"
            );
            return;
        }

        var spec = MockableSpec.TryCreate(symbol);
        if (spec == null)
        {
            Log.LogError($"[{TestProjectDisplay}] Overwrites.Mockable does not support the provided expression: {bodyExpr.WithoutTrivia()}");
            return;
        }

        // Ensure unique dependency member names
        var baseName = spec.DependencyMemberName;
        var finalName = baseName;
        int i = 1;
        while (mockables.Any(m => string.Equals(m.DependencyMemberName, finalName, StringComparison.Ordinal)))
        {
            finalName = $"{baseName}_{i}";
            i++;
        }

        mockables.Add(spec with { DependencyMemberName = finalName });
    }

    private sealed class MockableAndDependenciesRewriter : CSharpSyntaxRewriter
    {
        private readonly string _targetClassName;
        private readonly string _targetTypeFullName;
        private readonly IReadOnlyList<MockableSpec> _mockables;
        private readonly string _dependenciesInterfaceName;
        private readonly string _dependenciesFieldName;
        private readonly string _dependenciesParameterName;

        private bool _insideTargetClass;
        private bool _addedInterface;
        private bool _needsCallerMemberNameUsing;

        public MockableAndDependenciesRewriter(
            string targetClassName,
            string targetTypeFullName,
            IReadOnlyList<MockableSpec> mockables,
            string dependenciesInterfaceName,
            string dependenciesFieldName,
            string dependenciesParameterName)
        {
            _targetClassName = targetClassName;
            _targetTypeFullName = targetTypeFullName;
            _mockables = mockables;
            _dependenciesInterfaceName = dependenciesInterfaceName;
            _dependenciesFieldName = dependenciesFieldName;
            _dependenciesParameterName = dependenciesParameterName;
        }

        public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
        {
            var updated = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;

            if (_needsCallerMemberNameUsing)
            {
                var already = updated.Usings.Any(u => u.Name?.ToString() == "System.Runtime.CompilerServices");
                if (!already)
                {
                    var u = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Runtime.CompilerServices"))
                        .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
                    updated = updated.AddUsings(u);
                }
            }

            if (!_addedInterface)
            {
                var hasTargetClassAtRoot = updated.Members.OfType<ClassDeclarationSyntax>().Any(c => c.Identifier.Text == _targetClassName);
                if (hasTargetClassAtRoot)
                {
                    if (!updated.Members.OfType<InterfaceDeclarationSyntax>().Any(i => i.Identifier.Text == _dependenciesInterfaceName))
                    {
                        updated = updated.AddMembers(BuildDependenciesInterface());
                    }
                    _addedInterface = true;
                }
            }

            return updated;
        }

        public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        {
            var updated = (FileScopedNamespaceDeclarationSyntax)base.VisitFileScopedNamespaceDeclaration(node)!;

            if (!_addedInterface && ContainsTargetClass(updated.Members))
            {
                if (!updated.Members.OfType<InterfaceDeclarationSyntax>().Any(i => i.Identifier.Text == _dependenciesInterfaceName))
                    updated = updated.AddMembers(BuildDependenciesInterface());
                _addedInterface = true;
            }

            return updated;
        }

        public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            var updated = (NamespaceDeclarationSyntax)base.VisitNamespaceDeclaration(node)!;

            if (!_addedInterface && ContainsTargetClass(updated.Members))
            {
                if (!updated.Members.OfType<InterfaceDeclarationSyntax>().Any(i => i.Identifier.Text == _dependenciesInterfaceName))
                    updated = updated.AddMembers(BuildDependenciesInterface());
                _addedInterface = true;
            }

            return updated;
        }

        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            if (node.Identifier.Text != _targetClassName)
                return base.VisitClassDeclaration(node);

            var prev = _insideTargetClass;
            _insideTargetClass = true;

            var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;

            _insideTargetClass = prev;

            if (!visited.Members.OfType<FieldDeclarationSyntax>().Any(f =>
                    f.Declaration.Variables.Any(v => v.Identifier.Text == _dependenciesFieldName)))
            {
                var field = SyntaxFactory.FieldDeclaration(
                        SyntaxFactory.VariableDeclaration(
                            SyntaxFactory.IdentifierName(_dependenciesInterfaceName),
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(_dependenciesFieldName))
                            )
                        )
                    )
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword))
                    .WithTrailingTrivia(
                        SyntaxFactory.ElasticCarriageReturnLineFeed,
                        SyntaxFactory.ElasticCarriageReturnLineFeed
                    );

                visited = visited.WithMembers(visited.Members.Insert(0, field));
            }

            var newMembers = new List<MemberDeclarationSyntax>(visited.Members.Count);
            foreach (var m in visited.Members)
            {
                if (m is ConstructorDeclarationSyntax ctor)
                    newMembers.Add(InjectDependenciesIntoConstructor(ctor));
                else
                    newMembers.Add(m);
            }

            visited = visited.WithMembers(SyntaxFactory.List(newMembers));
            return visited;

            bool ContainsTargetClass(SyntaxList<MemberDeclarationSyntax> members)
                => members.OfType<ClassDeclarationSyntax>().Any(c => c.Identifier.Text == _targetClassName);
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            if (!_insideTargetClass) return base.VisitInvocationExpression(node);

            MockableSpec? spec = null;
            if (node.Expression is IdentifierNameSyntax id)
            {
                spec = _mockables.FirstOrDefault(s =>
                    s.Kind == MockableKind.Method
                    && s.IsInstanceMember
                    && s.MemberName == id.Identifier.Text);
            }
            else if (node.Expression is MemberAccessExpressionSyntax mae)
            {
                var memberName = mae.Name.Identifier.Text;
                if (mae.Expression is ThisExpressionSyntax)
                {
                    spec = _mockables.FirstOrDefault(s =>
                        s.Kind == MockableKind.Method
                        && s.IsInstanceMember
                        && s.MemberName == memberName);
                }
                else
                {
                    var containingTypeSimple = GetLastIdentifier(mae.Expression);
                    spec = _mockables.FirstOrDefault(s =>
                        s.Kind == MockableKind.Method
                        && s.MemberName == memberName
                        && s.ContainingTypeSimpleName == containingTypeSimple);
                }
            }

            if (spec != null)
            {
                if (spec.Parameters.Any(p => p.HasCallerMemberNameAttribute))
                    _needsCallerMemberNameUsing = true;

                var newExpr = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(_dependenciesFieldName),
                    SyntaxFactory.IdentifierName(spec.DependencyMemberName)
                );

                return node.WithExpression(newExpr).WithTriviaFrom(node);
            }

            return base.VisitInvocationExpression(node);
        }

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            if (_insideTargetClass)
            {
                var containingTypeSimple = GetLastIdentifier(node.Expression);
                var memberName = node.Name.Identifier.Text;

                var spec = _mockables.FirstOrDefault(s =>
                    (s.Kind == MockableKind.Property || s.Kind == MockableKind.Field)
                    && s.MemberName == memberName
                    && s.ContainingTypeSimpleName == containingTypeSimple);

                if (spec != null)
                {
                    var original = node.WithoutTrivia().ToString();

                    var invocation = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName(_dependenciesFieldName),
                            SyntaxFactory.IdentifierName(spec.DependencyMemberName)
                        ),
                        SyntaxFactory.ArgumentList()
                    );

                    var comment = SyntaxFactory.Comment($"/* Original: {original} */");

                    return invocation
                        .WithTrailingTrivia(
                            SyntaxFactory.TriviaList(
                                SyntaxFactory.Space,
                                comment
                            )
                        )
                        .WithLeadingTrivia(node.GetLeadingTrivia());
                }
            }

            return base.VisitMemberAccessExpression(node);
        }

        private ConstructorDeclarationSyntax InjectDependenciesIntoConstructor(ConstructorDeclarationSyntax ctor)
        {
            var hasParamAlready = ctor.ParameterList.Parameters.Any(p =>
                p.Type?.ToString() == _dependenciesInterfaceName);

            if (!hasParamAlready)
            {
                var paramName = ctor.ParameterList.Parameters.Any(p => p.Identifier.Text == _dependenciesParameterName)
                    ? _dependenciesParameterName + "2"
                    : _dependenciesParameterName;

                var newParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName))
                    .WithType(SyntaxFactory.IdentifierName(_dependenciesInterfaceName)).WithTrailingTrivia(SyntaxFactory.Space);

                ctor = ctor.WithParameterList(
                    ctor.ParameterList.AddParameters(newParam)
                );

                if (ctor.Body != null)
                {
                    var assignment = SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName(_dependenciesFieldName),
                            SyntaxFactory.IdentifierName(paramName)
                        )
                    );

                    ctor = ctor.WithBody(ctor.Body.WithStatements(ctor.Body.Statements.Insert(0, assignment)));
                }
            }

            return ctor;
        }

        private InterfaceDeclarationSyntax BuildDependenciesInterface()
        {
            var members = new List<MemberDeclarationSyntax>();

            foreach (var m in _mockables)
            {
                if (m.Kind == MockableKind.Method)
                {
                    if (m.Parameters.Any(p => p.HasCallerMemberNameAttribute))
                        _needsCallerMemberNameUsing = true;

                    var parameters = m.Parameters.Select(BuildParameterSyntax);
                    var method = SyntaxFactory.MethodDeclaration(
                            SyntaxFactory.ParseTypeName(m.ReturnTypeDisplay ?? "void"),
                            SyntaxFactory.Identifier(m.DependencyMemberName)
                        )
                        .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)))
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

                    members.Add(method);
                }
                else
                {
                    var prop = SyntaxFactory.PropertyDeclaration(
                            SyntaxFactory.ParseTypeName(m.DelegateTypeDisplay),
                            SyntaxFactory.Identifier(m.DependencyMemberName)
                        )
                        .WithAccessorList(
                            SyntaxFactory.AccessorList(
                                SyntaxFactory.SingletonList(
                                    SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                                )
                            )
                        );

                    members.Add(prop);
                }
            }

            var iface = SyntaxFactory.InterfaceDeclaration(_dependenciesInterfaceName)
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                .WithLeadingTrivia(
                    SyntaxFactory.TriviaList(
                        SyntaxFactory.Trivia(
                            SyntaxFactory.DocumentationCommentTrivia(SyntaxKind.SingleLineDocumentationCommentTrivia,
                                SyntaxFactory.List(new XmlNodeSyntax[]
                                {
                                    SyntaxFactory.XmlText("/// "),
                                    SyntaxFactory.XmlElement(
                                        SyntaxFactory.XmlElementStartTag(SyntaxFactory.XmlName("summary")),
                                        SyntaxFactory.SingletonList<XmlNodeSyntax>(
                                            SyntaxFactory.XmlText("Mock this using NSubstitute")
                                        ),
                                        SyntaxFactory.XmlElementEndTag(SyntaxFactory.XmlName("summary"))
                                    ),
                                    SyntaxFactory.XmlText(Environment.NewLine)
                                })
                            )
                        )
                    )
                )
                .WithMembers(SyntaxFactory.List(members))
                .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);

            return iface;
        }

        private static ParameterSyntax BuildParameterSyntax(MockableParameter p)
        {
            var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier(p.Name))
                .WithType(SyntaxFactory.ParseTypeName(p.TypeDisplay));

            if (p.HasCallerMemberNameAttribute)
            {
                var attr = SyntaxFactory.Attribute(SyntaxFactory.IdentifierName("CallerMemberName"));
                param = param.WithAttributeLists(
                    SyntaxFactory.SingletonList(
                        SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attr))
                    )
                );
            }

            if (p.HasExplicitDefaultValue)
            {
                if (p.ExplicitDefaultValue is null)
                {
                    ExpressionSyntax nullExpr;
                    if (p.NullableAnnotation == NullableAnnotation.Annotated)
                        nullExpr = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
                    else
                        nullExpr = SyntaxFactory.PostfixUnaryExpression(
                            SyntaxKind.SuppressNullableWarningExpression,
                            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
                        );

                    param = param.WithDefault(SyntaxFactory.EqualsValueClause(nullExpr));
                }
                else if (p.ExplicitDefaultValue is string s)
                {
                    param = param.WithDefault(
                        SyntaxFactory.EqualsValueClause(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(s)))
                    );
                }
                else if (p.ExplicitDefaultValue is bool b)
                {
                    param = param.WithDefault(
                        SyntaxFactory.EqualsValueClause(
                            b
                                ? SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression)
                                : SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)
                        )
                    );
                }
                else if (p.ExplicitDefaultValue is int i)
                {
                    param = param.WithDefault(
                        SyntaxFactory.EqualsValueClause(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(i)))
                    );
                }
                else
                {
                    param = param.WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.IdentifierName("default")));
                }
            }

            return param;
        }

        private static bool ContainsTargetClass(SyntaxList<MemberDeclarationSyntax> members)
            => members.OfType<ClassDeclarationSyntax>().Any(c => c.Identifier.Text != null);

        private static string? GetLastIdentifier(ExpressionSyntax expr)
        {
            return expr switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax mae => mae.Name.Identifier.Text,
                QualifiedNameSyntax qn => qn.Right.Identifier.Text,
                AliasQualifiedNameSyntax aqn => aqn.Name.Identifier.Text,
                _ => expr.ToString().Split('.').LastOrDefault()
            };
        }
    }

    private enum MockableKind
    {
        Property,
        Field,
        Method
    }

    private sealed record MockableParameter(
        string Name,
        string TypeDisplay,
        NullableAnnotation NullableAnnotation,
        bool HasExplicitDefaultValue,
        object? ExplicitDefaultValue,
        bool HasCallerMemberNameAttribute
    );

    private sealed record MockableSpec(
        MockableKind Kind,
        string ContainingTypeSimpleName,
        string ContainingTypeFullName,
        string MemberName,
        string DelegateTypeDisplay,
        string DependencyMemberName,
        IReadOnlyList<MockableParameter> Parameters,
        string? ReturnTypeDisplay,
        bool IsInstanceMember
    )
    {
        public static MockableSpec? TryCreate(ISymbol symbol)
        {
            var containingType = symbol.ContainingType;
            if (containingType == null)
                return null;

            var containingTypeSimple = containingType.Name;
            var containingTypeFull = containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var isInstance = !symbol.IsStatic;

            switch (symbol)
            {
                case IPropertySymbol p:
                    return new MockableSpec(
                        Kind: MockableKind.Property,
                        ContainingTypeSimpleName: containingTypeSimple,
                        ContainingTypeFullName: containingTypeFull,
                        MemberName: p.Name,
                        DelegateTypeDisplay: BuildDelegateTypeDisplay(Array.Empty<ITypeSymbol>(), p.Type),
                        DependencyMemberName: p.Name,
                        Parameters: Array.Empty<MockableParameter>(),
                        ReturnTypeDisplay: p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        IsInstanceMember: isInstance
                    );

                case IFieldSymbol f:
                    return new MockableSpec(
                        Kind: MockableKind.Field,
                        ContainingTypeSimpleName: containingTypeSimple,
                        ContainingTypeFullName: containingTypeFull,
                        MemberName: f.Name,
                        DelegateTypeDisplay: BuildDelegateTypeDisplay(Array.Empty<ITypeSymbol>(), f.Type),
                        DependencyMemberName: f.Name,
                        Parameters: Array.Empty<MockableParameter>(),
                        ReturnTypeDisplay: f.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        IsInstanceMember: isInstance
                    );

                case IMethodSymbol m:
                    if (m.MethodKind != MethodKind.Ordinary)
                        return null;

                    var paramTypes = m.Parameters.Select(pp => pp.Type).ToArray();
                    var parameters = m.Parameters.Select(p => new MockableParameter(
                        Name: string.IsNullOrWhiteSpace(p.Name) ? "param" : p.Name,
                        TypeDisplay: p.Type.ToDisplayString(
                            SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier)
                        ),
                        NullableAnnotation: p.Type.NullableAnnotation,
                        HasExplicitDefaultValue: p.HasExplicitDefaultValue,
                        ExplicitDefaultValue: p.HasExplicitDefaultValue ? p.ExplicitDefaultValue : null,
                        HasCallerMemberNameAttribute: p.GetAttributes().Any(a =>
                            a.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.CallerMemberNameAttribute")
                    )).ToList();

                    return new MockableSpec(
                        Kind: MockableKind.Method,
                        ContainingTypeSimpleName: containingTypeSimple,
                        ContainingTypeFullName: containingTypeFull,
                        MemberName: m.Name,
                        DelegateTypeDisplay: BuildDelegateTypeDisplay(paramTypes, m.ReturnType),
                        DependencyMemberName: m.Name,
                        Parameters: parameters,
                        ReturnTypeDisplay: m.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        IsInstanceMember: isInstance
                    );

                default:
                    return null;
            }
        }

        private static string BuildDelegateTypeDisplay(IReadOnlyList<ITypeSymbol> parameterTypes, ITypeSymbol returnType)
        {
            static string TypeDisplay(ITypeSymbol t) =>
                t.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier)
                );

            if (returnType.SpecialType == SpecialType.System_Void)
            {
                if (parameterTypes.Count == 0)
                    return "global::System.Action";

                var args = string.Join(", ", parameterTypes.Select(TypeDisplay));
                return $"global::System.Action<{args}>";
            }
            else
            {
                if (parameterTypes.Count == 0)
                    return $"global::System.Func<{TypeDisplay(returnType)}>";

                var args = string.Join(", ", parameterTypes.Select(TypeDisplay).Concat(new[] { TypeDisplay(returnType) }));
                return $"global::System.Func<{args}>";
            }
        }
    }
}