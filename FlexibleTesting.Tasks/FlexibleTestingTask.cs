using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FlexibleTesting.Tasks;

public class FlexibleTestingTask : Microsoft.Build.Utilities.Task
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
                            solution,
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

    private sealed record FlexibleTestingInstructions(
        INamedTypeSymbol TargetType,
        string OldClassName,
        string NewClassName,
        IReadOnlyList<IMethodSymbol> MethodsToMakePublic,
        IReadOnlyList<MockableSpec> Mockables,
        string DependenciesInterfaceName,
        string DependenciesFieldName,
        string DependenciesParameterName
    );

    private void GenerateForFlexibleTesting(
        Solution solution,
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

        var document = solution.GetDocument(targetClassNode.SyntaxTree);
        if (document == null)
        {
            Log.LogError(
                $"[{TestProjectDisplay}] Could not find Document for target class '{targetTypeInLegacy.ToDisplayString()}' in the solution."
            );
            return;
        }

        var instructions = new FlexibleTestingInstructions(
            TargetType: targetTypeInLegacy,
            OldClassName: oldName,
            NewClassName: newName,
            MethodsToMakePublic: methodsToMakePublicInLegacy,
            Mockables: mockablesFromTest,
            DependenciesInterfaceName: $"IAuto{oldName}Dependencies",
            DependenciesFieldName: "_dependencies",
            DependenciesParameterName: "dependencies"
        );

        var rewrittenDocument = ApplyRewritesAsync(document, targetClassNode, instructions).GetAwaiter().GetResult();
        var newRoot = rewrittenDocument.GetSyntaxRootAsync().GetAwaiter().GetResult();

        var normalized = newRoot!.NormalizeWhitespace(elasticTrivia: true).ToFullString();

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

    private async Task<Document> ApplyRewritesAsync(
        Document document,
        ClassDeclarationSyntax classNode,
        FlexibleTestingInstructions instructions
    )
    {
        var editor = await DocumentEditor.CreateAsync(document);
        var generator = editor.Generator;
        var semanticModel = await document.GetSemanticModelAsync();

        if (semanticModel == null)
        {
            Log.LogError($"[{TestProjectDisplay}] Could not get semantic model for target class.");
            return document;
        }

        bool needsCallerMemberName = false;
        var ctors = classNode.Members.OfType<ConstructorDeclarationSyntax>().ToList();

        // Pre-extract method symbols from the original semantic model before any editor operations
        // This avoids "Syntax node is not within syntax tree" errors inside ReplaceNode callbacks
        var methodSymbolsMap = new Dictionary<string, IMethodSymbol>();
        foreach (var member in classNode.Members.OfType<MethodDeclarationSyntax>())
        {
            var symbol = semanticModel.GetDeclaredSymbol(member);
            if (symbol != null)
            {
                var key = $"{member.Identifier.Text}:{GetMethodSignature(symbol)}";
                methodSymbolsMap[key] = symbol;
            }
        }

        // Phase 1: Transform the class structure (rename, add field, update constructors, make methods public)
        // Do NOT try to replace mockables here - the semantic model is bound to the original tree
        editor.ReplaceNode(
            classNode,
            (oldClass, gen) =>
            {
                var updatedClass = (ClassDeclarationSyntax)oldClass;

                // 1. Rename class
                updatedClass = (ClassDeclarationSyntax)gen.WithName(updatedClass, instructions.NewClassName);

                // 2. Add dependency injection field
                var fieldDecl = (FieldDeclarationSyntax)
                    gen.FieldDeclaration(
                        instructions.DependenciesFieldName,
                        gen.IdentifierName(instructions.DependenciesInterfaceName),
                        Accessibility.Private,
                        DeclarationModifiers.ReadOnly
                    );
                updatedClass = updatedClass.AddMembers(fieldDecl);

                // 3. Build new members list with transformed constructors and public methods
                var newMembers = new List<MemberDeclarationSyntax>();
                var hasExistingCtors = false;

                foreach (var member in updatedClass.Members)
                {
                    if (member is ConstructorDeclarationSyntax ctor)
                    {
                        hasExistingCtors = true;
                        var updatedCtor = RenameAndInjectDependencyIntoCtor(ctor, gen, instructions, ref needsCallerMemberName);
                        newMembers.Add(updatedCtor);
                    }
                    else if (member is MethodDeclarationSyntax method)
                    {
                        // Make methods public if they're in the list
                        // Use pre-extracted symbol map instead of GetDeclaredSymbol to avoid tree binding issues
                        var key = $"{method.Identifier.Text}:{GetMethodSignatureFromSyntax(method)}";
                        var symbol = methodSymbolsMap.TryGetValue(key, out var sym) ? sym : null;
                        MemberDeclarationSyntax updatedMethod = method;

                        if (symbol != null && instructions.MethodsToMakePublic.Any(m => SymbolsMatch(m, symbol)))
                        {
                            updatedMethod = (MethodDeclarationSyntax)gen.WithAccessibility(method, Accessibility.Public);
                        }

                        newMembers.Add(updatedMethod);
                    }
                    else if (
                        member is FieldDeclarationSyntax field
                        && field.Declaration.Variables.Any(v => v.Identifier.Text == instructions.DependenciesFieldName)
                    )
                    {
                        // Keep the dependency field we added
                        newMembers.Add(field);
                    }
                    else
                    {
                        // Keep other members as-is for now (will process mockables in phase 2)
                        newMembers.Add((MemberDeclarationSyntax)member);
                    }
                }

                // If no constructors exist, create one
                if (!hasExistingCtors)
                {
                    var defaultCtor = CreateDefaultDependencyInjectionConstructor(gen, instructions);
                    newMembers.Add(defaultCtor);
                }

                // Replace all members
                updatedClass = updatedClass.WithMembers(SyntaxFactory.List(newMembers));
                return updatedClass;
            }
        );

        // Phase 2a: Get a new document (after Phase 1 transformations)
        var changedDoc = editor.GetChangedDocument();

        // Phase 2b: Replace mockables with fresh semantic model bound to the current tree
        // We do this AFTER ReplaceNode completes to ensure semantic model is properly bound
        var editor2b = await DocumentEditor.CreateAsync(changedDoc);
        var generator2b = editor2b.Generator;
        var phase2bSemanticModel = await changedDoc.GetSemanticModelAsync();

        if (phase2bSemanticModel != null)
        {
            // Find the updated class in the tree
            var phase2bRoot = await changedDoc.GetSyntaxRootAsync();
            var phase2bClass = phase2bRoot
                ?.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == instructions.NewClassName);

            if (phase2bClass != null)
            {
                // Apply mockable replacements at the document level (not inside a ReplaceNode callback)
                // This ensures semantic model operations work correctly with the actual tree
                var (doc, callerMemberName) = await ApplyMockableReplacements(
                    changedDoc,
                    phase2bClass,
                    phase2bSemanticModel,
                    instructions,
                    needsCallerMemberName
                );
                changedDoc = doc;
                needsCallerMemberName = callerMemberName;
            }
        }

        // Phase 3: Add the dependencies interface
        var editor3 = await DocumentEditor.CreateAsync(changedDoc);
        var generator3 = editor3.Generator;
        var root3 = await changedDoc.GetSyntaxRootAsync();

        var finalClass = root3
            ?.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == instructions.NewClassName);

        if (finalClass != null)
        {
            var interfaceDecl = BuildDependenciesInterface(generator3, instructions);
            editor3.InsertAfter(finalClass, interfaceDecl);
            changedDoc = editor3.GetChangedDocument();
        }

        // Phase 4: Add using for System.Runtime.CompilerServices if needed
        if (needsCallerMemberName)
        {
            var editor4 = await DocumentEditor.CreateAsync(changedDoc);
            var generator4 = editor4.Generator;
            var root4 = await changedDoc.GetSyntaxRootAsync();

            if (root4 is CompilationUnitSyntax cu)
            {
                if (!cu.Usings.Any(u => u.Name?.ToString() == "System.Runtime.CompilerServices"))
                {
                    var newUsing = (UsingDirectiveSyntax)generator4.NamespaceImportDeclaration("System.Runtime.CompilerServices");
                    editor4.ReplaceNode(cu, (n, g) => ((CompilationUnitSyntax)n).AddUsings(newUsing));
                    changedDoc = editor4.GetChangedDocument();
                }
            }
        }

        return changedDoc;
    }

    private async Task<(Document Document, bool NeedsCallerMemberName)> ApplyMockableReplacements(
        Document document,
        ClassDeclarationSyntax classNode,
        SemanticModel semanticModel,
        FlexibleTestingInstructions instructions,
        bool needsCallerMemberName
    )
    {
        var editor = await DocumentEditor.CreateAsync(document);
        var generator = editor.Generator;

        // Process all members and replace mockables
        // This happens outside ReplaceNode callback to ensure semantic model is properly bound
        var root = await document.GetSyntaxRootAsync();
        if (root == null)
            return (document, needsCallerMemberName);

        var currentRoot = root;

        // Find the class in the current root
        var classInTree = currentRoot
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == instructions.NewClassName);

        if (classInTree == null)
            return (document, needsCallerMemberName);

        // Build list of member replacements
        var memberReplacements = new List<(SyntaxNode oldMember, SyntaxNode newMember)>();

        foreach (var member in classInTree.Members)
        {
            // Replace mockables in each member
            // Note: needsCallerMemberName is updated by ref inside ReplaceMockablesInNode
            var tempNeedsCallerMemberName = needsCallerMemberName;
            var memberWithMockablesReplaced = ReplaceMockablesInNode(
                member,
                generator,
                semanticModel,
                instructions,
                ref tempNeedsCallerMemberName
            );
            needsCallerMemberName = tempNeedsCallerMemberName;

            if (!member.IsEquivalentTo(memberWithMockablesReplaced))
            {
                memberReplacements.Add((member, memberWithMockablesReplaced));
            }
        }

        // Apply all replacements to the tree
        var newRoot = currentRoot;
        foreach (var (oldMember, newMember) in memberReplacements)
        {
            newRoot = newRoot.ReplaceNode(oldMember, newMember);
        }

        // If any replacements were made, update the document
        if (memberReplacements.Count > 0)
        {
            var newDocument = document.WithSyntaxRoot(newRoot);
            return (newDocument, needsCallerMemberName);
        }

        return (document, needsCallerMemberName);
    }

    private ConstructorDeclarationSyntax RenameAndInjectDependencyIntoCtor(
        ConstructorDeclarationSyntax ctor,
        SyntaxGenerator generator,
        FlexibleTestingInstructions instructions,
        ref bool needsCallerMemberName
    )
    {
        // Determine parameter name (avoid conflicts)
        var paramName = instructions.DependenciesParameterName;
        if (ctor.ParameterList.Parameters.Any(p => p.Identifier.Text == paramName))
        {
            paramName += "2";
        }

        // Create new parameter for dependency injection
        var newParam = (ParameterSyntax)
            generator.ParameterDeclaration(paramName, generator.IdentifierName(instructions.DependenciesInterfaceName));

        // Create assignment statement: this._dependencies = dependencies;
        var assignment = (StatementSyntax)
            generator.ExpressionStatement(
                generator.AssignmentStatement(
                    generator.IdentifierName(instructions.DependenciesFieldName),
                    generator.IdentifierName(paramName)
                )
            );

        // Build new constructor body
        BlockSyntax newBody;
        if (ctor.Body != null)
        {
            // Add assignment at the beginning of existing body
            var existingStatements = ctor.Body.Statements;
            if (existingStatements.Count > 0)
            {
                newBody = SyntaxFactory.Block(new[] { assignment }.Concat(existingStatements).ToArray());
            }
            else
            {
                newBody = ctor.Body.AddStatements(assignment);
            }
        }
        else if (ctor.ExpressionBody != null)
        {
            // Convert expression body to block body
            var expr = SyntaxFactory.ExpressionStatement((ExpressionSyntax)ctor.ExpressionBody.Expression);
            newBody = SyntaxFactory.Block(assignment, expr);
        }
        else
        {
            // No body or expression body - create new block with just assignment
            newBody = SyntaxFactory.Block(assignment);
        }

        // Create new constructor with the dependency parameter
        var newCtor = ctor.WithIdentifier(SyntaxFactory.Identifier(instructions.NewClassName))
            .WithParameterList(ctor.ParameterList.AddParameters(newParam))
            .WithBody(newBody)
            .WithExpressionBody(null)
            .WithSemicolonToken(default);

        return newCtor;
    }

    private ConstructorDeclarationSyntax CreateDefaultDependencyInjectionConstructor(
        SyntaxGenerator generator,
        FlexibleTestingInstructions instructions
    )
    {
        var paramName = instructions.DependenciesParameterName;
        var newParam = (ParameterSyntax)
            generator.ParameterDeclaration(paramName, generator.IdentifierName(instructions.DependenciesInterfaceName));

        var assignment = (StatementSyntax)
            generator.ExpressionStatement(
                generator.AssignmentStatement(
                    generator.IdentifierName(instructions.DependenciesFieldName),
                    generator.IdentifierName(paramName)
                )
            );

        var newCtor = (ConstructorDeclarationSyntax)
            generator.ConstructorDeclaration(
                parameters: new[] { newParam },
                accessibility: Accessibility.Public,
                statements: new[] { assignment }
            );

        return newCtor.WithIdentifier(SyntaxFactory.Identifier(instructions.NewClassName));
    }

    private SyntaxNode ReplaceMockablesInNode(
        SyntaxNode node,
        SyntaxGenerator generator,
        SemanticModel semanticModel,
        FlexibleTestingInstructions instructions,
        ref bool needsCallerMemberName
    )
    {
        // Build a list of replacements with tracking of node positions to handle stale references
        var replacements = new List<(SyntaxNode oldNode, SyntaxNode newNode)>();

        var descendantNodes = node.DescendantNodes().Where(n => n is InvocationExpressionSyntax or MemberAccessExpressionSyntax).ToList();

        var nodesToReplaceWithSpecs = new List<(SyntaxNode Node, MockableSpec Spec)>();

        foreach (var n in descendantNodes)
        {
            var spec = GetMockableSpecForNode(n, semanticModel, instructions.Mockables);
            if (spec != null)
            {
                nodesToReplaceWithSpecs.Add((n, spec));
            }
        }

        // Highest-node-only to avoid double-replacing (e.g. Guid.NewGuid() vs Guid.NewGuid)
        var finalNodesToReplace = nodesToReplaceWithSpecs
            .Where(x => !nodesToReplaceWithSpecs.Any(y => x.Node != y.Node && x.Node.Ancestors().Contains(y.Node)))
            .ToList();

        // Build all replacements first, then apply them with ReplaceNodes to avoid stale references
        foreach (var (nodeToReplace, spec) in finalNodesToReplace)
        {
            if (spec.Parameters.Any(p => p.HasCallerMemberNameAttribute))
                needsCallerMemberName = true;

            SyntaxNode replacement;
            if (spec.Kind == MockableKind.Method)
            {
                var args = nodeToReplace is InvocationExpressionSyntax inv
                    ? inv.ArgumentList.Arguments.Select(a => a.Expression)
                    : Enumerable.Empty<SyntaxNode>();

                replacement = generator.InvocationExpression(
                    generator.MemberAccessExpression(
                        generator.IdentifierName(instructions.DependenciesFieldName),
                        spec.DependencyMemberName
                    ),
                    args
                );
            }
            else
            {
                replacement = generator.InvocationExpression(
                    generator.MemberAccessExpression(
                        generator.IdentifierName(instructions.DependenciesFieldName),
                        spec.DependencyMemberName
                    )
                );
            }

            replacements.Add((nodeToReplace, replacement.WithTriviaFrom(nodeToReplace)));
        }

        // Apply all replacements at once using ReplaceNodes if possible, otherwise sequentially with fresh lookups
        var currentNode = node;
        foreach (var (oldNode, newNode) in replacements)
        {
            // Search for a node that matches the original in the current tree
            var matchingNode = currentNode.DescendantNodesAndSelf().FirstOrDefault(n => n.IsEquivalentTo(oldNode));

            if (matchingNode != null)
            {
                currentNode = currentNode.ReplaceNode(matchingNode, newNode);
            }
        }

        return currentNode;
    }

    private static bool SymbolsMatch(ISymbol? a, ISymbol? b)
    {
        if (a == null || b == null)
            return false;
        if (a.Name != b.Name)
            return false;

        if (a is IMethodSymbol ma && b is IMethodSymbol mb)
        {
            if (ma.Parameters.Length != mb.Parameters.Length)
                return false;
            for (int i = 0; i < ma.Parameters.Length; i++)
            {
                if (ma.Parameters[i].Type.ToDisplayString() != mb.Parameters[i].Type.ToDisplayString())
                    return false;
            }
            return true;
        }
        return false;
    }

    private static string GetMethodSignature(IMethodSymbol symbol)
    {
        var paramTypes = string.Join(",", symbol.Parameters.Select(p => p.Type.ToDisplayString()));
        return $"{paramTypes}";
    }

    private static string GetMethodSignatureFromSyntax(MethodDeclarationSyntax method)
    {
        var paramTypes = string.Join(",", method.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? ""));
        return $"{paramTypes}";
    }

    private static MockableSpec? GetMockableSpecForNode(SyntaxNode node, SemanticModel semanticModel, IReadOnlyList<MockableSpec> mockables)
    {
        var symbol = semanticModel.GetSymbolInfo(node).Symbol;
        if (symbol == null && node is MemberAccessExpressionSyntax mae)
            symbol = semanticModel.GetSymbolInfo(mae.Name).Symbol;
        if (symbol == null && node is InvocationExpressionSyntax inv)
            symbol = semanticModel.GetSymbolInfo(inv.Expression).Symbol;

        if (symbol == null)
            return null;

        var fullQualifiedName = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        return mockables.FirstOrDefault(m => m.MemberName == symbol.Name && m.ContainingTypeFullName == fullQualifiedName);
    }

    private static SyntaxNode BuildDependenciesInterface(SyntaxGenerator generator, FlexibleTestingInstructions instructions)
    {
        var members = new List<SyntaxNode>();

        foreach (var m in instructions.Mockables)
        {
            if (m.Kind == MockableKind.Method)
            {
                var parameters = m.Parameters.Select(p =>
                {
                    var param = generator.ParameterDeclaration(p.Name, generator.IdentifierName(p.TypeDisplay));
                    if (p.HasCallerMemberNameAttribute)
                    {
                        param = generator.AddAttributes(param, generator.Attribute("CallerMemberName"));
                    }
                    if (p.HasExplicitDefaultValue)
                    {
                        param = ((ParameterSyntax)param).WithDefault(
                            SyntaxFactory.EqualsValueClause((ExpressionSyntax)generator.LiteralExpression(p.ExplicitDefaultValue))
                        );
                    }
                    return param;
                });

                SyntaxNode returnType;
                if (string.IsNullOrWhiteSpace(m.ReturnTypeDisplay) || m.ReturnTypeDisplay == "System.Void" || m.ReturnTypeDisplay == "void")
                {
                    returnType = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword));
                }
                else
                {
                    returnType = generator.IdentifierName(m.ReturnTypeDisplay);
                }

                var method = generator.MethodDeclaration(
                    m.DependencyMemberName,
                    parameters: parameters,
                    returnType: returnType,
                    accessibility: Accessibility.Public
                );
                members.Add(method);
            }
            else
            {
                var prop = generator.PropertyDeclaration(
                    m.DependencyMemberName,
                    generator.IdentifierName(m.DelegateTypeDisplay),
                    accessibility: Accessibility.Public,
                    getAccessorStatements: null
                );
                members.Add(prop);
            }
        }

        var iface = generator.InterfaceDeclaration(
            instructions.DependenciesInterfaceName,
            accessibility: Accessibility.Public,
            members: members
        );

        var comment = SyntaxFactory.Comment("/// <summary>Mock this using NSubstitute</summary>");
        return ((SyntaxNode)iface).WithLeadingTrivia(SyntaxFactory.TriviaList(comment));
    }

    private static List<IMethodSymbol> MapMethodsToLegacy(INamedTypeSymbol legacyType, List<IMethodSymbol> methodsFromTest)
    {
        var legacyMethods = legacyType.GetMembers().OfType<IMethodSymbol>().ToList();
        var result = new List<IMethodSymbol>();

        foreach (var mb in methodsFromTest)
        {
            var match = legacyMethods.FirstOrDefault(ml => SymbolsMatch(ml, mb));
            if (match != null)
                result.Add(match);
        }

        return result;
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
            symbols.AddRange(
                defineConstants
                    .Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
            );
        }

        return new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: symbols);
    }

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
        if (Path.IsPathRooted(paths[0]))
        {
            var firstDir = Path.GetDirectoryName(paths[0]) ?? "";
            var rootFull = firstDir;
            while (!string.IsNullOrEmpty(rootFull) && !rootFull.EndsWith(root, StringComparison.OrdinalIgnoreCase))
                rootFull = Path.GetDirectoryName(rootFull) ?? "";
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

    private void AddToMockable(SemanticModel semanticModel, List<MockableSpec> mockables, InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList.Arguments.Count == 0)
            return;
        var argument = invocation.ArgumentList.Arguments[0].Expression;
        if (argument is not LambdaExpressionSyntax lambda)
            return;
        if (
            lambda switch
            {
                ParenthesizedLambdaExpressionSyntax pl => pl.ParameterList.Parameters.Count,
                SimpleLambdaExpressionSyntax => 1,
                _ => 0,
            } != 0
        )
        {
            Log.LogError($"[{TestProjectDisplay}] Overwrites.Mockable only supports parameterless lambdas: Overwrites.Mockable(() => ...)");
            return;
        }
        ExpressionSyntax? bodyExpr = lambda.Body as ExpressionSyntax;
        if (bodyExpr == null && lambda.Body is BlockSyntax block)
            bodyExpr =
                block.Statements.OfType<ReturnStatementSyntax>().Select(r => r.Expression).FirstOrDefault(e => e != null)
                as ExpressionSyntax;
        if (bodyExpr == null)
        {
            Log.LogError(
                $"[{TestProjectDisplay}] Overwrites.Mockable lambda body must be an expression (or a block with a return expression)."
            );
            return;
        }
        var symbol = semanticModel.GetSymbolInfo(bodyExpr).Symbol;
        if (symbol == null && bodyExpr is MemberAccessExpressionSyntax mae)
            symbol = semanticModel.GetSymbolInfo(mae.Name).Symbol;
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
            Log.LogError(
                $"[{TestProjectDisplay}] Overwrites.Mockable does not support the provided expression: {bodyExpr.WithoutTrivia()}"
            );
            return;
        }
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

    private enum MockableKind
    {
        Property,
        Field,
        Method,
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
                    var parameters = m
                        .Parameters.Select(p => new MockableParameter(
                            Name: string.IsNullOrWhiteSpace(p.Name) ? "param" : p.Name,
                            TypeDisplay: p.Type.ToDisplayString(
                                SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
                                    SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
                                )
                            ),
                            NullableAnnotation: p.Type.NullableAnnotation,
                            HasExplicitDefaultValue: p.HasExplicitDefaultValue,
                            ExplicitDefaultValue: p.HasExplicitDefaultValue ? p.ExplicitDefaultValue : null,
                            HasCallerMemberNameAttribute: p.GetAttributes()
                                .Any(a =>
                                    a.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.CallerMemberNameAttribute"
                                )
                        ))
                        .ToList();
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
                    SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
                        SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
                    )
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
