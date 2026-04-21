using FlexibleTestingDomain;
using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.MSBuild;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FlexibleTesting.Tasks;

// Usefull info about someone who does something similar (he mocks the mocks to make them compile time instead of runtime): https://github.com/dotnet/roslyn/issues/4974
public class FlexibleTestingTask : Microsoft.Build.Utilities.Task
{
    /// <summary>
    /// Default output path is e.g., ..\TestProject\Generated
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Should be filled with path to LegacyProejct (e.g., ..\LegacyCodeProject\LegacyCodeProject.csproj), DLL's do not work!
    /// </summary>
    [Required]
    public string LegacyProjectPath { get; set; } = string.Empty;

    public override bool Execute()
    {
        try
        {
            System.Diagnostics.Debugger.Launch();

            // Use provided OutputPath or default to ../Generated directory
            OutputPath = string.IsNullOrWhiteSpace(OutputPath)
                ? Path.GetFullPath(OutputPath!)
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(BuildEngine.ProjectFileOfTaskNode)!, "Generated"));

            var properties = new Dictionary<string, string>
            {
                ["DesignTimeBuild"] = "true",
                ["BuildingInsideVisualStudio"] = "true",
                ["SkipCompilerExecution"] = "true",
                ["BuildProjectReferences"] = "false",
                ["ProvideCommandLineArgs"] = "true",
                ["FlexibleTestingTaskRunning"] = "true",
            };
            using var msBuildWorkspace = MSBuildWorkspace.Create(properties);
            msBuildWorkspace.SkipUnrecognizedProjects = true;
            var legacyProject = msBuildWorkspace.OpenProjectAsync(LegacyProjectPath).Result;
            var legacyCompilation = legacyProject.GetCompilationAsync().Result;
            var testProject = msBuildWorkspace.OpenProjectAsync(BuildEngine.ProjectFileOfTaskNode).Result;
            var testCompilation = testProject.GetCompilationAsync().Result;

            FindBuilders(legacyProject, legacyCompilation, testCompilation);

            return true;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: true);
            return false;
        }
    }

    private void FindBuilders(Project legacyProject, Compilation legacyComp, Compilation testComp)
    {
        var targetSymbol = testComp.GetTypeByMetadataName(typeof(GeneratorInstructionsAttribute).FullName!);

        foreach (var tree in testComp.SyntaxTrees)
        {
            ProcessTree(tree, legacyProject, legacyComp, testComp, targetSymbol);
        }
    }

    private void ProcessTree(SyntaxTree tree, Project project, Compilation legacyComp, Compilation testComp, INamedTypeSymbol? targetSymbol)
    {
        var model = testComp.GetSemanticModel(tree);
        var classes = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classNode in classes)
        {
            if (IsTargetBuilder(classNode, model, targetSymbol))
            {
                GenerateForFlexibleTesting(project, legacyComp, model, classNode);
            }
        }
    }

    private bool IsTargetBuilder(ClassDeclarationSyntax classNode, SemanticModel model, ISymbol? targetSymbol)
    {
        var symbol = model.GetDeclaredSymbol(classNode);
        if (symbol == null)
        {
            return false;
        }
        return symbol.GetAttributes().Any(a => a.AttributeClass?.IsEqualToSymbol(targetSymbol) ?? false);
    }

    private void GenerateForFlexibleTesting(
        Project project,
        Compilation legacyCompilation,
        SemanticModel builderSemanticModel,
        ClassDeclarationSyntax classNode
    )
    {
        var configureMethod = classNode
            .Members.OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.ValueText == nameof(IGeneratorInstructions.Configure));

        if (configureMethod?.Body == null)
        {
            return;
        }

        var methodsToMakePublicFromTest = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var mockablesFromTest = new List<MockableSpec>();
        INamedTypeSymbol? targetTypeFromTest = null;
        var mockInheritanceFromTest = false;

        var overwritesSymbol = builderSemanticModel.Compilation.GetTypeByMetadataName(typeof(Overwrites).FullName!);

        // All method calls inside the Configure() body, like 'Overwrites.Mock<UserService>()'
        var allInstructionMethods = configureMethod.Body.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var instructionMethod in allInstructionMethods)
        {
            if (builderSemanticModel.GetSymbolInfo(instructionMethod).Symbol is not IMethodSymbol methodSymbol)
            {
                // Skip invalid methods
                continue;
            }

            if (!methodSymbol.IsDeclaredIn(overwritesSymbol)) // Sanity check, only process methods from the Overwrites class
            {
                continue;
            }

            switch (methodSymbol.Name)
            {
                case nameof(Overwrites.ForClass):
                    targetTypeFromTest = methodSymbol.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
                    break;

                case nameof(Overwrites.MakePublic):
                    AddToMakePublic(builderSemanticModel, methodsToMakePublicFromTest, instructionMethod);
                    break;

                case nameof(Overwrites.Mockable):
                    AddToMockable(builderSemanticModel, mockablesFromTest, instructionMethod);
                    break;

                case nameof(Overwrites.MockInheritance):
                    mockInheritanceFromTest = true;
                    break;

                case nameof(Overwrites.Mock):
                    // TODO: Implement Mock
                    break;
            }
        }

        if (targetTypeFromTest == null || targetTypeFromTest.TypeKind == TypeKind.Error)
        {
            return;
        }

        // Re-resolve the type in the legacy SOURCE compilation so it becomes a SOURCE symbol
        var targetMetadataName = GetTypeMetadataName(targetTypeFromTest.OriginalDefinition);
        var targetTypeInLegacy = legacyCompilation.GetTypeByMetadataName(targetMetadataName);

        if (targetTypeInLegacy == null)
        {
            return;
        }

        var typeSyntaxRef = targetTypeInLegacy.DeclaringSyntaxReferences.FirstOrDefault();
        if (typeSyntaxRef == null)
        {
            return;
        }

        if (typeSyntaxRef.GetSyntax() is not ClassDeclarationSyntax targetClassNode)
        {
            return;
        }

        // For each mockable method, also add it to methodsToMakePublic so it becomes public
        foreach (var mockable in mockablesFromTest.Where(mockableSpec => mockableSpec.Kind == MockableKind.Method))
        {
            // Find the method in the target type from the test compilation
            var mockableMethod = targetTypeFromTest
                .GetMembers(mockable.MemberName)
                .OfType<IMethodSymbol>()
                .FirstOrDefault(methodSymbol => methodSymbol.ContainingType?.Name == mockable.ContainingTypeSimpleName);

            // If not found in the target type, search in base types
            if (mockableMethod == null)
            {
                var currentType = targetTypeFromTest.BaseType;
                while (currentType != null && currentType.SpecialType != SpecialType.System_Object)
                {
                    mockableMethod = currentType
                        .GetMembers(mockable.MemberName)
                        .OfType<IMethodSymbol>()
                        .FirstOrDefault(methodSymbol => methodSymbol.ContainingType?.Name == mockable.ContainingTypeSimpleName);

                    if (mockableMethod != null)
                    {
                        break;
                    }

                    currentType = currentType.BaseType;
                }
            }

            if (mockableMethod != null)
            {
                methodsToMakePublicFromTest.Add(mockableMethod);

                // Also add the base definition if this is an override
                var originalDefinition = mockableMethod.OriginalDefinition;
                if (mockableMethod.IsOverride && originalDefinition.OverriddenMethod != null)
                {
                    var baseMethod = mockableMethod.OverriddenMethod;
                    while (baseMethod?.IsOverride == true && baseMethod.OverriddenMethod != null)
                    {
                        baseMethod = baseMethod.OverriddenMethod;
                    }

                    if (baseMethod != null)
                    {
                        methodsToMakePublicFromTest.Add(baseMethod);
                    }
                }
            }
        }

        // IMPORTANT: symbols from testCompilation won't match symbols from legacyCompilation,
        // so map methods-to-make-public by signature.
        var methodsToMakePublicInLegacy = MapMethodsToLegacy(targetTypeInLegacy, methodsToMakePublicFromTest);

        var oldName = targetClassNode.Identifier.Text;
        var newName = $"{oldName}_G";

        var document = project.GetDocument(targetClassNode.SyntaxTree);
        if (document == null)
        {
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
            DependenciesParameterName: "dependencies",
            MockInheritance: mockInheritanceFromTest
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
    }

    private async Task<Document> ApplyRewritesAsync(
        Document document,
        ClassDeclarationSyntax classNode,
        FlexibleTestingInstructions instructions
    )
    {
        var documentEditor = await DocumentEditor.CreateAsync(document);
        var syntaxGenerator = documentEditor.Generator;
        var semanticModel = await document.GetSemanticModelAsync();

        if (semanticModel == null)
        {
            return document;
        }

        bool needsCallerMemberName = false;
        var constructors = classNode.Members.OfType<ConstructorDeclarationSyntax>().ToList();

        // Pre-extract method symbols from the original semantic model before any editor operations
        // This avoids "Syntax node is not within syntax tree" errors inside ReplaceNode callbacks
        var methodSymbolsMap = new Dictionary<string, IMethodSymbol>();
        foreach (var member in classNode.Members.OfType<MethodDeclarationSyntax>())
        {
            var methodSymbol = semanticModel.GetDeclaredSymbol(member);
            if (methodSymbol != null)
            {
                var key = $"{member.Identifier.Text}:{GetMethodSignature(methodSymbol)}";
                methodSymbolsMap[key] = methodSymbol;
            }
        }

        // Phase 1: Transform the class structure (rename, add field, update constructors, make methods public)
        // Do NOT try to replace mockables here - the semantic model is bound to the original tree
        documentEditor.ReplaceNode(
            classNode,
            (oldClassNode, generator) =>
            {
                var updatedClassNode = (ClassDeclarationSyntax)oldClassNode;

                // 1. Rename class
                updatedClassNode = (ClassDeclarationSyntax)generator.WithName(updatedClassNode, instructions.NewClassName);

                // 1b. If MockInheritance, update base class reference
                if (instructions.MockInheritance && updatedClassNode.BaseList != null)
                {
                    var baseClassName = $"{instructions.OldClassName}Base_G";
                    var newBaseList = updatedClassNode.BaseList.WithTypes(
                        SyntaxFactory.SeparatedList(
                            [(BaseTypeSyntax)SyntaxFactory.SimpleBaseType(SyntaxFactory.IdentifierName(baseClassName))]
                        )
                    );
                    updatedClassNode = updatedClassNode.WithBaseList(newBaseList);
                }

                // 2. Add dependency injection field
                var fieldDeclaration = (FieldDeclarationSyntax)
                    generator.FieldDeclaration(
                        instructions.DependenciesFieldName,
                        generator.IdentifierName(instructions.DependenciesInterfaceName),
                        Accessibility.Private,
                        DeclarationModifiers.ReadOnly
                    );
                updatedClassNode = updatedClassNode.AddMembers(fieldDeclaration);

                // 2b. If MockInheritance, add base dependencies field
                if (instructions.MockInheritance)
                {
                    var baseDependenciesFieldName = "_baseDependencies";
                    var baseDependenciesInterfaceName = $"IAuto{instructions.OldClassName}BaseDependencies";
                    var baseDependenciesField = (FieldDeclarationSyntax)
                        generator.FieldDeclaration(
                            baseDependenciesFieldName,
                            generator.IdentifierName(baseDependenciesInterfaceName),
                            Accessibility.Private,
                            DeclarationModifiers.ReadOnly
                        );
                    updatedClassNode = updatedClassNode.AddMembers(baseDependenciesField);
                }

                // 3. Build new members list with transformed constructors and public methods
                var newMembers = new List<MemberDeclarationSyntax>();
                var hasExistingConstructors = false;

                foreach (var member in updatedClassNode.Members)
                {
                    if (member is ConstructorDeclarationSyntax constructorDeclaration)
                    {
                        hasExistingConstructors = true;
                        var updatedConstructor = instructions.MockInheritance
                            ? RenameAndInjectDependenciesIntoCtor(constructorDeclaration, generator, instructions)
                            : RenameAndInjectDependencyIntoCtor(constructorDeclaration, generator, instructions, ref needsCallerMemberName);
                        newMembers.Add(updatedConstructor);
                    }
                    else if (member is MethodDeclarationSyntax methodDeclaration)
                    {
                        // Make methods public if they're in the list
                        // Use pre-extracted symbol map instead of GetDeclaredSymbol to avoid tree binding issues
                        var key = $"{methodDeclaration.Identifier.Text}:{GetMethodSignatureFromSyntax(methodDeclaration)}";
                        var methodSymbol = methodSymbolsMap.TryGetValue(key, out var symbol) ? symbol : null;
                        MemberDeclarationSyntax updatedMethod = methodDeclaration;

                        if (methodSymbol != null && instructions.MethodsToMakePublic.Any(m => SymbolsMatch(m, methodSymbol)))
                        {
                            updatedMethod = MakeMethodPublic((MethodDeclarationSyntax)updatedMethod);
                        }

                        newMembers.Add(updatedMethod);
                    }
                    else if (
                        member is FieldDeclarationSyntax fieldDeclarationNode
                        && (
                            fieldDeclarationNode.Declaration.Variables.Any(v => v.Identifier.Text == instructions.DependenciesFieldName)
                            || (
                                instructions.MockInheritance
                                && fieldDeclarationNode.Declaration.Variables.Any(v => v.Identifier.Text == "_baseDependencies")
                            )
                        )
                    )
                    {
                        // Keep the dependency fields we added
                        newMembers.Add(fieldDeclarationNode);
                    }
                    else
                    {
                        // Keep other members as-is for now (will process mockables in phase 2)
                        newMembers.Add((MemberDeclarationSyntax)member);
                    }
                }

                // If no constructors exist, create one
                if (!hasExistingConstructors)
                {
                    var defaultConstructor = instructions.MockInheritance
                        ? CreateDefaultDependencyInjectionConstructorWithBase(generator, instructions)
                        : CreateDefaultDependencyInjectionConstructor(generator, instructions);
                    newMembers.Add(defaultConstructor);
                }

                // Replace all members
                updatedClassNode = updatedClassNode.WithMembers(SyntaxFactory.List(newMembers));
                return updatedClassNode;
            }
        );

        // Phase 2a: Get a new document (after Phase 1 transformations)
        var changedDocument = documentEditor.GetChangedDocument();

        // Phase 2b: Replace mockables with fresh semantic model bound to the current tree
        // We do this AFTER ReplaceNode completes to ensure semantic model is properly bound
        var documentEditor2b = await DocumentEditor.CreateAsync(changedDocument);
        var syntaxGenerator2b = documentEditor2b.Generator;
        var phase2bSemanticModel = await changedDocument.GetSemanticModelAsync();

        if (phase2bSemanticModel != null)
        {
            // Find the updated class in the tree
            var phase2bRoot = await changedDocument.GetSyntaxRootAsync();
            var phase2bClass = phase2bRoot
                ?.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(classDeclaration => classDeclaration.Identifier.Text == instructions.NewClassName);

            if (phase2bClass != null)
            {
                // Apply mockable replacements at the document level (not inside a ReplaceNode callback)
                // This ensures semantic model operations work correctly with the actual tree
                var (newDoc, callerMemberNameNeeded) = await ApplyMockableReplacements(
                    changedDocument,
                    phase2bClass,
                    phase2bSemanticModel,
                    instructions,
                    needsCallerMemberName
                );
                changedDocument = newDoc;
                needsCallerMemberName = callerMemberNameNeeded;
            }
        }

        // Phase 3: Add the dependencies interface
        var documentEditor3 = await DocumentEditor.CreateAsync(changedDocument);
        var syntaxGenerator3 = documentEditor3.Generator;
        var root3 = await changedDocument.GetSyntaxRootAsync();

        var finalClassNode = root3
            ?.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(classDeclaration => classDeclaration.Identifier.Text == instructions.NewClassName);

        if (finalClassNode != null)
        {
            var interfaceDeclaration = BuildDependenciesInterface(syntaxGenerator3, instructions);
            documentEditor3.InsertAfter(finalClassNode, interfaceDeclaration);
            changedDocument = documentEditor3.GetChangedDocument();
        }

        // Phase 3b: Handle MockInheritance - generate base class replacement and dependencies
        if (instructions.MockInheritance && classNode.BaseList != null && classNode.BaseList.Types.Any())
        {
            // Extract which base members are actually used
            var baseType = instructions.TargetType.BaseType;
            if (baseType != null && baseType.SpecialType != SpecialType.System_Object)
            {
                var usedBaseMembers = ExtractUsedBaseMembers(classNode, instructions.TargetType, await document.GetSemanticModelAsync());

                if (usedBaseMembers.Any())
                {
                    var documentEditor3b = await DocumentEditor.CreateAsync(changedDocument);
                    var syntaxGenerator3b = documentEditor3b.Generator;
                    var root3b = await changedDocument.GetSyntaxRootAsync();

                    var updatedClassNode = root3b
                        ?.DescendantNodes()
                        .OfType<ClassDeclarationSyntax>()
                        .FirstOrDefault(classDeclaration => classDeclaration.Identifier.Text == instructions.NewClassName);

                    if (updatedClassNode != null)
                    {
                        var baseClassName = $"{instructions.OldClassName}Base_G";
                        var baseDependenciesInterfaceName = $"IAuto{instructions.OldClassName}BaseDependencies";

                        // Generate the base class replacement
                        var baseClassDeclaration = BuildBaseClassReplacement(
                            syntaxGenerator3b,
                            instructions.OldClassName,
                            baseType,
                            usedBaseMembers,
                            baseDependenciesInterfaceName,
                            instructions.DependenciesFieldName,
                            instructions.MethodsToMakePublic
                        );

                        // Generate the base dependencies interface
                        var baseDependenciesInterfaceDeclaration = BuildBaseDependenciesInterface(
                            syntaxGenerator3b,
                            instructions.OldClassName,
                            usedBaseMembers
                        );

                        // Insert base class and interface before the derived class
                        documentEditor3b.InsertBefore(updatedClassNode, baseClassDeclaration);
                        documentEditor3b.InsertBefore(updatedClassNode, baseDependenciesInterfaceDeclaration);

                        changedDocument = documentEditor3b.GetChangedDocument();

                        // If any base members have CallerMemberName attributes, we'll need the using statement
                        var callerMemberNameAttributeName = "CallerMemberNameAttribute";
                        if (
                            usedBaseMembers
                                .OfType<IMethodSymbol>()
                                .Any(methodSymbol =>
                                    methodSymbol.Parameters.Any(parameterSymbol => parameterSymbol.GetAttributes().Any(attributeData => attributeData.AttributeClass?.Name == callerMemberNameAttributeName))
                                )
                        )
                        {
                            needsCallerMemberName = true;
                        }
                    }
                }
            }
        }

        // Phase 4: Add using for System.Runtime.CompilerServices if needed
        if (needsCallerMemberName)
        {
            var documentEditor4 = await DocumentEditor.CreateAsync(changedDocument);
            var syntaxGenerator4 = documentEditor4.Generator;
            var root4 = await changedDocument.GetSyntaxRootAsync();

            var compilerServicesNamespace = "System.Runtime.CompilerServices";
            if (root4 is CompilationUnitSyntax compilationUnit)
            {
                if (!compilationUnit.Usings.Any(usingDirective => usingDirective.Name?.ToString() == compilerServicesNamespace))
                {
                    var newUsingDirective = (UsingDirectiveSyntax)syntaxGenerator4.NamespaceImportDeclaration(compilerServicesNamespace);
                    documentEditor4.ReplaceNode(compilationUnit, (node, generator) => ((CompilationUnitSyntax)node).AddUsings(newUsingDirective));
                    changedDocument = documentEditor4.GetChangedDocument();
                }
            }
        }

        return changedDocument;
    }

    private async Task<(Document Document, bool NeedsCallerMemberName)> ApplyMockableReplacements(
        Document document,
        ClassDeclarationSyntax classNode,
        SemanticModel semanticModel,
        FlexibleTestingInstructions instructions,
        bool needsCallerMemberName
    )
    {
        var documentEditor = await DocumentEditor.CreateAsync(document);
        var syntaxGenerator = documentEditor.Generator;

        // Process all members and replace mockables
        // This happens outside ReplaceNode callback to ensure semantic model is properly bound
        var syntaxRoot = await document.GetSyntaxRootAsync();
        if (syntaxRoot == null)
        {
            return (document, needsCallerMemberName);
        }

        var currentSyntaxRoot = syntaxRoot;

        // Find the class in the current root
        var classInTree = currentSyntaxRoot
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(classDeclaration => classDeclaration.Identifier.Text == instructions.NewClassName);

        if (classInTree == null)
        {
            return (document, needsCallerMemberName);
        }

        // Build list of member replacements
        var memberReplacements = new List<(SyntaxNode OldMember, SyntaxNode NewMember)>();

        foreach (var member in classInTree.Members)
        {
            // Replace mockables in each member
            // Note: needsCallerMemberName is updated by ref inside ReplaceMockablesInNode
            var tempNeedsCallerMemberName = needsCallerMemberName;
            var memberWithMockablesReplaced = ReplaceMockablesInNode(
                member,
                syntaxGenerator,
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

        // Apply all replacements to the tree at once using ReplaceNodes
        // This avoids issues with stale node references when applying replacements sequentially
        if (memberReplacements.Count > 0)
        {
            var replacementDictionary = memberReplacements.ToDictionary(replacement => replacement.OldMember, replacement => replacement.NewMember);
            var newSyntaxRoot = currentSyntaxRoot.ReplaceNodes(replacementDictionary.Keys, (oldNode, _) => replacementDictionary[oldNode]);
            var newDocument = document.WithSyntaxRoot(newSyntaxRoot);
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
            generator.ConstructorDeclaration(parameters: [newParam], accessibility: Accessibility.Public, statements: [assignment]);

        return newCtor.WithIdentifier(SyntaxFactory.Identifier(instructions.NewClassName));
    }

    private ConstructorDeclarationSyntax RenameAndInjectDependenciesIntoCtor(
        ConstructorDeclarationSyntax ctor,
        SyntaxGenerator generator,
        FlexibleTestingInstructions instructions
    )
    {
        // Determine parameter names (avoid conflicts)
        var depsParamName = instructions.DependenciesParameterName;
        if (ctor.ParameterList.Parameters.Any(p => p.Identifier.Text == depsParamName))
        {
            depsParamName += "2";
        }

        var baseDepsParamName = "baseDependencies";
        if (ctor.ParameterList.Parameters.Any(p => p.Identifier.Text == baseDepsParamName))
        {
            baseDepsParamName = "baseDependencies2";
        }

        // Create new parameters for both dependency injections
        var depsParam = (ParameterSyntax)
            generator.ParameterDeclaration(depsParamName, generator.IdentifierName(instructions.DependenciesInterfaceName));
        var baseDepsParam = (ParameterSyntax)
            generator.ParameterDeclaration(
                baseDepsParamName,
                generator.IdentifierName($"IAuto{instructions.OldClassName}BaseDependencies")
            );

        // Create assignment statements
        var depsAssignment = (StatementSyntax)
            generator.ExpressionStatement(
                generator.AssignmentStatement(
                    generator.IdentifierName(instructions.DependenciesFieldName),
                    generator.IdentifierName(depsParamName)
                )
            );

        var baseDepsAssignment = (StatementSyntax)
            generator.ExpressionStatement(
                generator.AssignmentStatement(generator.IdentifierName("_baseDependencies"), generator.IdentifierName(baseDepsParamName))
            );

        // Update base() call to pass baseDependencies
        ConstructorInitializerSyntax? newInitializer = null;
        if (ctor.Initializer != null && ctor.Initializer.IsKind(SyntaxKind.BaseConstructorInitializer))
        {
            var baseArgs = ctor.Initializer.ArgumentList.Arguments.Add(
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName(baseDepsParamName))
            );
            newInitializer = ctor.Initializer.WithArgumentList(ctor.Initializer.ArgumentList.WithArguments(baseArgs));
        }

        // Build new constructor body
        BlockSyntax newBody;
        if (ctor.Body != null)
        {
            var existingStatements = ctor.Body.Statements;
            if (existingStatements.Count > 0)
            {
                newBody = SyntaxFactory.Block(new[] { depsAssignment, baseDepsAssignment }.Concat(existingStatements).ToArray());
            }
            else
            {
                newBody = SyntaxFactory.Block(depsAssignment, baseDepsAssignment);
            }
        }
        else if (ctor.ExpressionBody != null)
        {
            var expr = SyntaxFactory.ExpressionStatement((ExpressionSyntax)ctor.ExpressionBody.Expression);
            newBody = SyntaxFactory.Block(depsAssignment, baseDepsAssignment, expr);
        }
        else
        {
            newBody = SyntaxFactory.Block(depsAssignment, baseDepsAssignment);
        }

        // Create new constructor with both dependency parameters
        var newCtor = ctor.WithIdentifier(SyntaxFactory.Identifier(instructions.NewClassName))
            .WithParameterList(ctor.ParameterList.AddParameters(depsParam, baseDepsParam))
            .WithBody(newBody)
            .WithExpressionBody(null)
            .WithSemicolonToken(default);

        if (newInitializer != null)
        {
            newCtor = newCtor.WithInitializer(newInitializer);
        }

        return newCtor;
    }

    private ConstructorDeclarationSyntax CreateDefaultDependencyInjectionConstructorWithBase(
        SyntaxGenerator generator,
        FlexibleTestingInstructions instructions
    )
    {
        var depsParamName = instructions.DependenciesParameterName;
        var baseDepsParamName = "baseDependencies";

        var depsParam = (ParameterSyntax)
            generator.ParameterDeclaration(depsParamName, generator.IdentifierName(instructions.DependenciesInterfaceName));
        var baseDepsParam = (ParameterSyntax)
            generator.ParameterDeclaration(
                baseDepsParamName,
                generator.IdentifierName($"IAuto{instructions.OldClassName}BaseDependencies")
            );

        var depsAssignment = (StatementSyntax)
            generator.ExpressionStatement(
                generator.AssignmentStatement(
                    generator.IdentifierName(instructions.DependenciesFieldName),
                    generator.IdentifierName(depsParamName)
                )
            );

        var baseDepsAssignment = (StatementSyntax)
            generator.ExpressionStatement(
                generator.AssignmentStatement(generator.IdentifierName("_baseDependencies"), generator.IdentifierName(baseDepsParamName))
            );

        var newCtor = (ConstructorDeclarationSyntax)
            generator.ConstructorDeclaration(
                parameters: [depsParam, baseDepsParam],
                accessibility: Accessibility.Public,
                statements: [depsAssignment, baseDepsAssignment]
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
        var replacements = new List<(SyntaxNode OldNode, SyntaxNode NewNode)>();

        // FIX: Gebruik DescendantNodesAndSelf en zorg dat we diep genoeg graven (ook in accessors van properties)
        var descendantNodes = node.DescendantNodesAndSelf()
            .Where(descendantNode => descendantNode is InvocationExpressionSyntax or MemberAccessExpressionSyntax or AccessorDeclarationSyntax)
            .ToList();

        var nodesToReplaceWithSpecs = new List<(SyntaxNode Node, MockableSpec Spec)>();

        foreach (var descendantNode in descendantNodes)
        {
            var mockableSpec = GetMockableSpecForNode(descendantNode, semanticModel, instructions.Mockables);
            if (mockableSpec != null)
            {
                nodesToReplaceWithSpecs.Add((descendantNode, mockableSpec));
            }
        }

        // Highest-node-only to avoid double-replacing (e.g. Guid.NewGuid() vs Guid.NewGuid)
        var finalNodesToReplace = nodesToReplaceWithSpecs
            .Where(nodeWithSpec => !nodesToReplaceWithSpecs.Any(otherNodeWithSpec => nodeWithSpec.Node != otherNodeWithSpec.Node && nodeWithSpec.Node.Ancestors().Contains(otherNodeWithSpec.Node)))
            .ToList();

        // Build all replacements first, then apply them with ReplaceNodes to avoid stale references
        foreach (var (nodeToReplace, mockableSpec) in finalNodesToReplace)
        {
            if (mockableSpec.Parameters.Any(parameter => parameter.HasCallerMemberNameAttribute))
            {
                needsCallerMemberName = true;
            }

            SyntaxNode replacementNode;
            if (mockableSpec.Kind == MockableKind.Method)
            {
                var invocationArguments = nodeToReplace is InvocationExpressionSyntax invocationExpression
                    ? invocationExpression.ArgumentList.Arguments.Select(argument => argument.Expression)
                    : Enumerable.Empty<SyntaxNode>();

                replacementNode = generator.InvocationExpression(
                    generator.MemberAccessExpression(
                        generator.IdentifierName(instructions.DependenciesFieldName),
                        mockableSpec.DependencyMemberName
                    ),
                    invocationArguments
                );
            }
            else
            {
                replacementNode = generator.InvocationExpression(
                    generator.MemberAccessExpression(
                        generator.IdentifierName(instructions.DependenciesFieldName),
                        mockableSpec.DependencyMemberName
                    )
                );
            }

            replacements.Add((nodeToReplace, replacementNode.WithTriviaFrom(nodeToReplace)));
        }

        // Apply all replacements at once using ReplaceNodes to avoid stale node references
        if (replacements.Count > 0)
        {
            var replacementDictionary = replacements.ToDictionary(replacement => replacement.OldNode, replacement => replacement.NewNode);
            var resultNode = node.ReplaceNodes(replacementDictionary.Keys, (oldNode, _) => replacementDictionary[oldNode]);
            return resultNode;
        }

        return node;
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

    private static MethodDeclarationSyntax MakeMethodPublic(MethodDeclarationSyntax method)
    {
        // Remove existing accessibility modifiers (protected, private, internal, etc.)
        var modifiers = method
            .Modifiers.Where(m =>
                m.Kind()
                    is not SyntaxKind.ProtectedKeyword
                        and not SyntaxKind.PrivateKeyword
                        and not SyntaxKind.InternalKeyword
                        and not SyntaxKind.PublicKeyword
            )
            .ToList();

        // Remove the override modifier as we're converting this to a public method
        // that shouldn't override the base (the base will also be made public)
        modifiers = modifiers.Where(m => m.Kind() != SyntaxKind.OverrideKeyword).ToList();

        // Add public modifier at the beginning
        modifiers.Insert(0, SyntaxFactory.Token(SyntaxKind.PublicKeyword));

        return method.WithModifiers(SyntaxFactory.TokenList(modifiers));
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
        // Probeer direct het symbool op te halen voor de node
        var symbol = semanticModel.GetSymbolInfo(node).Symbol;

        // Als dat niet lukt, kijk specifiek naar de naam van de member access
        if (symbol == null && node is MemberAccessExpressionSyntax mae)
            symbol = semanticModel.GetSymbolInfo(mae.Name).Symbol;

        // Voor method calls: check de expressie (bijv. de methode-naam voor de haakjes)
        if (symbol == null && node is InvocationExpressionSyntax inv)
            symbol = semanticModel.GetSymbolInfo(inv.Expression).Symbol;

        // Als het een property is, vallen getters/setters onder IPropertySymbol
        if (symbol is IPropertySymbol || symbol is IMethodSymbol)
        {
            var fullQualifiedName = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return mockables.FirstOrDefault(m => m.MemberName == symbol.Name && m.ContainingTypeFullName == fullQualifiedName);
        }

        return null;
    }

    private static List<ISymbol> ExtractUsedBaseMembers(
        ClassDeclarationSyntax derivedClassNode,
        INamedTypeSymbol targetTypeSymbol,
        SemanticModel semanticModel
    )
    {
        var usedMembers = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var baseTypeSymbol = targetTypeSymbol.BaseType;

        if (baseTypeSymbol == null || baseTypeSymbol.SpecialType == SpecialType.System_Object)
        {
            return new List<ISymbol>();
        }

        // Get all invocations and member accesses in the derived class
        var allSyntaxNodes = derivedClassNode.DescendantNodes();

        foreach (var syntaxNode in allSyntaxNodes)
        {
            ISymbol? memberSymbol = null;

            switch (syntaxNode)
            {
                case InvocationExpressionSyntax invocationExpression:
                    // For method calls: base.OnLoad(), OnPropertyChanged(), etc.
                    memberSymbol = semanticModel.GetSymbolInfo(invocationExpression.Expression).Symbol;
                    break;

                case MemberAccessExpressionSyntax memberAccessExpression:
                    // For member access: base.OnLoad, this.SomeProperty, etc.
                    memberSymbol = semanticModel.GetSymbolInfo(memberAccessExpression).Symbol;
                    break;

                case IdentifierNameSyntax identifierName when identifierName.Parent is not MemberAccessExpressionSyntax:
                    // For simple identifiers: OnPropertyChanged, OnLoad (without base.), etc.
                    memberSymbol = semanticModel.GetSymbolInfo(identifierName).Symbol;
                    break;

                // TODO: Handle other cases if necessary (e.g., ObjectCreationExpressionSyntax, etc.)
            }

            // Check if symbol belongs to base type
            if (memberSymbol != null && baseTypeSymbol.GetMembers().Contains(memberSymbol, SymbolEqualityComparer.Default))
            {
                usedMembers.Add(memberSymbol);
            }
        }

        return usedMembers.ToList();
    }

    private static SyntaxNode BuildDependenciesInterface(SyntaxGenerator generator, FlexibleTestingInstructions instructions)
    {
        var members = new List<SyntaxNode>();

        foreach (var mockableSpec in instructions.Mockables)
        {
            if (mockableSpec.Kind == MockableKind.Method)
            {
                var methodParameters = mockableSpec.Parameters.Select(parameterSpec =>
                {
                    var parameterDeclaration = generator.ParameterDeclaration(parameterSpec.Name, generator.IdentifierName(parameterSpec.TypeDisplay));
                    if (parameterSpec.HasCallerMemberNameAttribute)
                    {
                        parameterDeclaration = generator.AddAttributes(parameterDeclaration, generator.Attribute("CallerMemberName"));
                    }
                    if (parameterSpec.HasExplicitDefaultValue)
                    {
                        parameterDeclaration = ((ParameterSyntax)parameterDeclaration).WithDefault(
                            SyntaxFactory.EqualsValueClause((ExpressionSyntax)generator.LiteralExpression(parameterSpec.ExplicitDefaultValue))
                        );
                    }
                    return parameterDeclaration;
                });

                SyntaxNode returnTypeNode;
                if (string.IsNullOrWhiteSpace(mockableSpec.ReturnTypeDisplay) || mockableSpec.ReturnTypeDisplay == typeof(void).FullName || mockableSpec.ReturnTypeDisplay == "void")
                {
                    returnTypeNode = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword));
                }
                else
                {
                    returnTypeNode = generator.IdentifierName(mockableSpec.ReturnTypeDisplay);
                }

                var methodDeclaration = generator.MethodDeclaration(
                    mockableSpec.DependencyMemberName,
                    parameters: methodParameters,
                    returnType: returnTypeNode,
                    accessibility: Accessibility.Public
                );
                members.Add(methodDeclaration);
            }
            else
            {
                var propertyDeclaration = generator.PropertyDeclaration(
                    mockableSpec.DependencyMemberName,
                    generator.IdentifierName(mockableSpec.DelegateTypeDisplay),
                    accessibility: Accessibility.Public,
                    getAccessorStatements: null
                );
                members.Add(propertyDeclaration);
            }
        }

        var interfaceDeclaration = generator.InterfaceDeclaration(
            instructions.DependenciesInterfaceName,
            accessibility: Accessibility.Public,
            members: members
        );

        var documentationComment = SyntaxFactory.Comment("/// <summary>Mock this using NSubstitute</summary>");
        return interfaceDeclaration.WithLeadingTrivia(SyntaxFactory.TriviaList(documentationComment));
    }

    private static SyntaxNode BuildBaseClassReplacement(
        SyntaxGenerator generator,
        string derivedClassName,
        INamedTypeSymbol baseType,
        List<ISymbol> usedMembers,
        string baseDependenciesInterfaceName,
        string dependenciesFieldName,
        IReadOnlyList<IMethodSymbol> methodsToMakePublic
    )
    {
        var baseClassName = $"{derivedClassName}Base_G";
        var baseDependenciesFieldName = "_baseDependencies";
        var baseDependenciesParameterName = "baseDependencies";

        var members = new List<MemberDeclarationSyntax>();

        // Add constructor with base type parameter and base dependencies injection
        var constructorParameters = new List<SyntaxNode>
        {
            generator.ParameterDeclaration(
                "someDataObject",
                generator.IdentifierName(baseType.Constructors.FirstOrDefault()?.Parameters.FirstOrDefault()?.Type.Name ?? "SomeDataObject")
            ),
            generator.ParameterDeclaration(baseDependenciesParameterName, generator.IdentifierName(baseDependenciesInterfaceName)),
        };

        var constructorBodyStatements = new List<StatementSyntax>
        {
            SyntaxFactory.ParseStatement($"{baseDependenciesFieldName} = {baseDependenciesParameterName};"),
        };

        var constructorDeclaration = (ConstructorDeclarationSyntax)
            generator.ConstructorDeclaration(baseClassName, parameters: constructorParameters, accessibility: Accessibility.Public);

        constructorDeclaration = constructorDeclaration.WithBody(SyntaxFactory.Block(constructorBodyStatements));
        members.Add(constructorDeclaration);

        // Add field for base dependencies
        var dependenciesField = (FieldDeclarationSyntax)
            generator.FieldDeclaration(
                baseDependenciesFieldName,
                generator.IdentifierName(baseDependenciesInterfaceName),
                Accessibility.Private,
                DeclarationModifiers.ReadOnly
            );
        members.Add(dependenciesField);

        // Add empty stub implementations for used base members
        foreach (var member in usedMembers)
        {
            if (member is IMethodSymbol methodSymbol && methodSymbol.MethodKind != MethodKind.Constructor)
            {
                var shouldBePublic = methodsToMakePublic.Any(methodToMakePublic => SymbolsMatch(methodToMakePublic, methodSymbol));
                var methodStub = CreateBaseMethodStub(
                    generator,
                    methodSymbol,
                    baseDependenciesInterfaceName,
                    baseDependenciesFieldName,
                    shouldBePublic
                );
                members.Add(methodStub);
            }
            else if (member is IPropertySymbol propertySymbol)
            {
                var propertyStub = CreateBasePropertyStub(generator, propertySymbol, baseDependenciesInterfaceName, baseDependenciesFieldName);
                members.Add(propertyStub);
            }
        }

        // Create the class
        var baseClassDeclaration = (ClassDeclarationSyntax)
            generator.ClassDeclaration(baseClassName, accessibility: Accessibility.Public, members: members);

        return baseClassDeclaration;
    }

    private static MethodDeclarationSyntax CreateBaseMethodStub(
        SyntaxGenerator generator,
        IMethodSymbol methodSymbol,
        string baseDependenciesInterfaceName,
        string baseDependenciesFieldName,
        bool shouldBePublic = false
    )
    {
        var methodName = methodSymbol.Name;
        var methodParameters = methodSymbol
            .Parameters.Select(parameterSymbol =>
            {
                var parameterTypeName = GetTypeNameSyntax(parameterSymbol.Type);
                var parameterDeclaration = (ParameterSyntax)generator.ParameterDeclaration(parameterSymbol.Name, parameterTypeName);

                // Add CallerMemberName attribute if present
                var callerMemberNameAttributeName = "CallerMemberNameAttribute";
                if (parameterSymbol.GetAttributes().Any(attributeData => attributeData.AttributeClass?.Name == callerMemberNameAttributeName))
                {
                    parameterDeclaration = generator.AddAttributes(parameterDeclaration, generator.Attribute("CallerMemberName")) as ParameterSyntax ?? parameterDeclaration;
                }

                // Add default value if parameter has one
                if (parameterSymbol.HasExplicitDefaultValue)
                {
                    var explicitDefaultValue = parameterSymbol.ExplicitDefaultValue;
                    var defaultExpression =
                        explicitDefaultValue == null
                            ? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
                            : (ExpressionSyntax)generator.LiteralExpression(explicitDefaultValue);
                    parameterDeclaration = parameterDeclaration.WithDefault(SyntaxFactory.EqualsValueClause(defaultExpression));
                }

                return parameterDeclaration;
            })
            .ToList();

        var returnTypeSyntax =
            methodSymbol.ReturnType.SpecialType == SpecialType.System_Void
                ? SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword))
                : GetTypeNameSyntax(methodSymbol.ReturnType);

        // Build method body that calls base dependencies
        // Generate: _baseDependencies.MethodName(param1, param2, ...)
        var parameterNames = methodSymbol.Parameters.Select(parameterSymbol => parameterSymbol.Name).ToList();
        var invocationArguments = parameterNames.Select(name => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(name))).ToArray();

        StatementSyntax bodyStatement;
        if (methodSymbol.ReturnType.SpecialType == SpecialType.System_Void)
        {
            // For void methods: _baseDependencies.MethodName(args);
            bodyStatement = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(baseDependenciesFieldName),
                        SyntaxFactory.IdentifierName(methodName)
                    ),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(invocationArguments))
                )
            );
        }
        else
        {
            // For non-void methods: return _baseDependencies.MethodName(args);
            bodyStatement = SyntaxFactory.ReturnStatement(
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(baseDependenciesFieldName),
                        SyntaxFactory.IdentifierName(methodName)
                    ),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(invocationArguments))
                )
            );
        }

        var methodBody = SyntaxFactory.Block(bodyStatement);

        // Determine accessibility: use public if shouldBePublic; otherwise if method is protected, use protected; otherwise public
        var accessibility = shouldBePublic
            ? Accessibility.Public
            : (methodSymbol.DeclaredAccessibility == Accessibility.Protected ? Accessibility.Protected : Accessibility.Public);

        var methodDeclaration = (MethodDeclarationSyntax)
            generator.MethodDeclaration(
                methodName,
                parameters: methodParameters,
                returnType: returnTypeSyntax,
                accessibility: accessibility,
                modifiers: DeclarationModifiers.Virtual
            );

        return methodDeclaration.WithBody(methodBody);
    }

    private static TypeSyntax GetTypeNameSyntax(ITypeSymbol typeSymbol)
    {
        TypeSyntax baseTypeSyntax;

        if (typeSymbol.SpecialType == SpecialType.System_String)
        {
            baseTypeSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword));
        }
        else if (typeSymbol.SpecialType == SpecialType.System_Object)
        {
            baseTypeSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
        }
        else if (typeSymbol.SpecialType == SpecialType.System_Void)
        {
            baseTypeSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword));
        }
        else if (typeSymbol.SpecialType == SpecialType.System_Int32)
        {
            baseTypeSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword));
        }
        else if (typeSymbol.SpecialType == SpecialType.System_Boolean)
        {
            baseTypeSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword));
        }
        else
        {
            // For all other types, use ParseTypeName to get proper TypeSyntax
            var fullyQualifiedName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            baseTypeSyntax = (TypeSyntax)SyntaxFactory.ParseTypeName(fullyQualifiedName);
        }

        // Check if type is nullable and wrap with NullableTypeSyntax if needed
        if (typeSymbol.IsReferenceType && typeSymbol.NullableAnnotation == NullableAnnotation.Annotated)
        {
            return SyntaxFactory.NullableType(baseTypeSyntax);
        }

        return baseTypeSyntax;
    }

    private static PropertyDeclarationSyntax CreateBasePropertyStub(
        SyntaxGenerator generator,
        IPropertySymbol propertySymbol,
        string baseDependenciesInterfaceName,
        string baseDependenciesFieldName
    )
    {
        var propertyTypeSyntax = GetTypeNameSyntax(propertySymbol.Type);
        var accessorList = new List<AccessorDeclarationSyntax>();

        if (propertySymbol.GetMethod != null)
        {
            var getter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration, body: SyntaxFactory.Block());
            accessorList.Add(getter);
        }

        if (propertySymbol.SetMethod != null)
        {
            var setter = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration, body: SyntaxFactory.Block());
            accessorList.Add(setter);
        }

        var propertyDeclaration = (PropertyDeclarationSyntax)
            generator.PropertyDeclaration(
                propertySymbol.Name,
                propertyTypeSyntax,
                accessibility: Accessibility.Public,
                modifiers: DeclarationModifiers.Virtual
            );

        if (accessorList.Count > 0)
        {
            propertyDeclaration = propertyDeclaration.WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessorList)));
        }

        return propertyDeclaration;
    }

    private static SyntaxNode BuildBaseDependenciesInterface(SyntaxGenerator generator, string derivedClassName, List<ISymbol> usedMembers)
    {
        var interfaceName = $"IAuto{derivedClassName}BaseDependencies";
        var members = new List<SyntaxNode>();

        foreach (var member in usedMembers)
        {
            if (member is IMethodSymbol methodSymbol && methodSymbol.MethodKind != MethodKind.Constructor)
            {
                var methodParameters = methodSymbol
                    .Parameters.Select(parameterSymbol =>
                    {
                        var parameterTypeName = GetTypeNameSyntax(parameterSymbol.Type);
                        var parameterDeclaration = (ParameterSyntax)generator.ParameterDeclaration(parameterSymbol.Name, parameterTypeName);

                        // Add CallerMemberName attribute if present
                        var callerMemberNameAttributeName = "CallerMemberNameAttribute";
                        if (parameterSymbol.GetAttributes().Any(attributeData => attributeData.AttributeClass?.Name == callerMemberNameAttributeName))
                        {
                            parameterDeclaration = generator.AddAttributes(parameterDeclaration, generator.Attribute("CallerMemberName")) as ParameterSyntax ?? parameterDeclaration;
                        }

                        // Add default value if parameter has one
                        if (parameterSymbol.HasExplicitDefaultValue)
                        {
                            var explicitDefaultValue = parameterSymbol.ExplicitDefaultValue;
                            var defaultExpression =
                                explicitDefaultValue == null
                                    ? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
                                    : (ExpressionSyntax)generator.LiteralExpression(explicitDefaultValue);
                            parameterDeclaration = parameterDeclaration.WithDefault(SyntaxFactory.EqualsValueClause(defaultExpression));
                        }

                        return parameterDeclaration;
                    })
                    .ToList();

                var returnTypeSyntax =
                    methodSymbol.ReturnType.SpecialType == SpecialType.System_Void
                        ? SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword))
                        : GetTypeNameSyntax(methodSymbol.ReturnType);

                var methodDeclaration = generator.MethodDeclaration(
                    methodSymbol.Name,
                    parameters: methodParameters,
                    returnType: returnTypeSyntax,
                    accessibility: Accessibility.Public
                );
                members.Add(methodDeclaration);
            }
            else if (member is IPropertySymbol propertySymbol)
            {
                var propertyTypeSyntax = GetTypeNameSyntax(propertySymbol.Type);
                var propertyDeclaration = generator.PropertyDeclaration(
                    propertySymbol.Name,
                    propertyTypeSyntax,
                    accessibility: Accessibility.Public,
                    getAccessorStatements: null
                );
                members.Add(propertyDeclaration);
            }
        }

        var interfaceDeclaration = generator.InterfaceDeclaration(interfaceName, accessibility: Accessibility.Public, members: members);

        var documentationComment = SyntaxFactory.Comment("/// <summary>Mock this using NSubstitute</summary>");
        return interfaceDeclaration.WithLeadingTrivia(SyntaxFactory.TriviaList(documentationComment));
    }

    private static List<IMethodSymbol> MapMethodsToLegacy(INamedTypeSymbol legacyTypeSymbol, HashSet<IMethodSymbol> methodsFromTest)
    {
        var legacyMethods = legacyTypeSymbol.GetMembers().OfType<IMethodSymbol>().ToList();
        var result = new List<IMethodSymbol>();

        foreach (var methodFromTest in methodsFromTest)
        {
            var match = legacyMethods.FirstOrDefault(legacyMethod => SymbolsMatch(legacyMethod, methodFromTest));
            if (match != null)
            {
                result.Add(match);
            }
            else
            {
                // If not found in the target type, try to find it in the base type
                // This handles virtual methods that need to be made public
                var currentBaseType = legacyTypeSymbol.BaseType;
                while (currentBaseType != null && currentBaseType.SpecialType != SpecialType.System_Object)
                {
                    var baseMatch = currentBaseType.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(legacyMethod => SymbolsMatch(legacyMethod, methodFromTest));
                    if (baseMatch != null)
                    {
                        result.Add(baseMatch);
                        break;
                    }
                    currentBaseType = currentBaseType.BaseType;
                }
            }
        }

        return result;
    }

    private static string GetTypeMetadataName(INamedTypeSymbol typeSymbol)
    {
        var originalDefinition = typeSymbol.OriginalDefinition;
        var namespaceName = originalDefinition.ContainingNamespace is { IsGlobalNamespace: false } ? originalDefinition.ContainingNamespace.ToDisplayString() : null;
        var typeParts = new Stack<string>();
        for (INamedTypeSymbol? currentTypeSymbol = originalDefinition; currentTypeSymbol != null; currentTypeSymbol = currentTypeSymbol.ContainingType)
        {
            typeParts.Push(currentTypeSymbol.MetadataName);
        }
        var typeName = string.Join("+", typeParts);
        return string.IsNullOrEmpty(namespaceName) ? typeName : $"{namespaceName}.{typeName}";
    }

    private void AddToMakePublic(
        SemanticModel semanticModel,
        HashSet<IMethodSymbol> methodsToMakePublic,
        InvocationExpressionSyntax invocationExpression
    )
    {
        if (invocationExpression.ArgumentList.Arguments.Count == 0)
        {
            return;
        }

        var argumentExpression = invocationExpression.ArgumentList.Arguments[0].Expression;
        if (argumentExpression is LambdaExpressionSyntax lambdaExpression)
        {
            SyntaxNode nodeToInspect = lambdaExpression.Body is InvocationExpressionSyntax invocationBody ? invocationBody.Expression : lambdaExpression.Body;
            var methodSymbol = semanticModel.GetSymbolInfo(nodeToInspect).Symbol as IMethodSymbol;
            if (methodSymbol != null)
            {
                methodsToMakePublic.Add(methodSymbol);
            }
        }
    }

    private void AddToMockable(SemanticModel semanticModel, List<MockableSpec> mockables, InvocationExpressionSyntax invocationExpression)
    {
        if (invocationExpression.ArgumentList.Arguments.Count == 0)
        {
            return;
        }

        var argumentExpression = invocationExpression.ArgumentList.Arguments.First().Expression;

        if (argumentExpression is not LambdaExpressionSyntax lambdaExpression)
        {
            if (argumentExpression is not IdentifierNameSyntax methodGroupIdentifier)
            {
                return;
            }

            var methodGroupSymbol = semanticModel.GetSymbolInfo(methodGroupIdentifier).Symbol;
            if (methodGroupSymbol is not IMethodSymbol methodSymbol)
            {
                return;
            }

            var methodSpec = MockableSpec.TryCreate(methodSymbol);
            if (methodSpec != null)
            {
                mockables.Add(methodSpec);
            }

            return;
        }

        if (lambdaExpression is SimpleLambdaExpressionSyntax)
        {
            return; // Ignore simple lambdas like Overwrites.Mockable(x => x.SomeMethod());
        }

        if (lambdaExpression is ParenthesizedLambdaExpressionSyntax parenthesizedLambda)
        {
            if (parenthesizedLambda.ParameterList.Parameters.Count > 0)
            {
                return; // Ignore lambda's with paramters like (x) => ... or (x, y) => ...
            }
        }

        var bodyExpression = lambdaExpression.Body switch
        {
            ExpressionSyntax expression => expression, // () => SomeMethod()
            BlockSyntax blockSyntax => blockSyntax.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault()?.Expression, // () => { return SomeMethod(); }
            _ => null,
        };

        if (bodyExpression is null)
        {
            return;
        }

        var symbol =
            semanticModel.GetSymbolInfo(bodyExpression).Symbol
            ?? bodyExpression switch
            {
                MemberAccessExpressionSyntax memberAccess => semanticModel.GetSymbolInfo(memberAccess.Name).Symbol,
                InvocationExpressionSyntax invocation => semanticModel.GetSymbolInfo(invocation.Expression).Symbol,
                _ => null,
            };

        if (symbol is not (IPropertySymbol or IFieldSymbol or IMethodSymbol))
        {
            return;
        }

        // Probeer een MockableSpec (definitie voor een mock) te maken op basis van het gevonden symbool.
        var mockableSpec = MockableSpec.TryCreate(symbol);
        if (mockableSpec == null)
        {
            return;
        }

        // Bepaal de basisnaam voor het dependency-lid.
        var baseName = mockableSpec.DependencyMemberName;
        var finalName = baseName;
        int duplicateSuffix = 1;

        // Voorkom dubbele namen in de lijst: als de naam al bestaat, voeg een nummer toe (bijv. _1, _2).
        while (mockables.Any(m => string.Equals(m.DependencyMemberName, finalName, StringComparison.Ordinal)))
        {
            finalName = $"{baseName}_{duplicateSuffix}";
            duplicateSuffix++;
        }

        // Voeg de specificatie toe aan de lijst met de (unieke) naam.
        mockables.Add(mockableSpec with { DependencyMemberName = finalMemberName });
    }
}
