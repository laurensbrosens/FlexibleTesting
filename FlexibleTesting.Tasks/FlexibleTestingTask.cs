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

    private void FindBuilders(Project legacyProject, Compilation? legacyComp, Compilation testComp)
    {
        var targetSymbol = testComp.GetTypeByMetadataName(typeof(GeneratorInstructionsAttribute).FullName!);

        var builders = testComp.SyntaxTrees.SelectMany(st =>
        {
            var model = testComp.GetSemanticModel(st);
            return st.GetRoot()
                .DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Select(node => (node, model, symbol: model.GetDeclaredSymbol(node)))
                .Where(t =>
                    t.symbol?.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, targetSymbol)) == true
                );
        });

        foreach (var (node, model, _) in builders)
        {
            GenerateForFlexibleTesting(legacyProject, testComp, legacyComp, model, node);
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
        string DependenciesParameterName,
        bool MockInheritance
    );

    private void GenerateForFlexibleTesting(
        Project project,
        Compilation testCompilation,
        Compilation? legacyCompilation,
        SemanticModel semanticModelB,
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

        var methodsToMakePublicFromTest = new List<IMethodSymbol>();
        var mockablesFromTest = new List<MockableSpec>();
        INamedTypeSymbol? targetTypeFromTest = null;
        var mockInheritanceFromTest = false;

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

                case "MockInheritance":
                    mockInheritanceFromTest = true;
                    break;
            }
        }

        if (targetTypeFromTest == null || targetTypeFromTest.TypeKind == TypeKind.Error)
        {
            return;
        }

        if (legacyCompilation == null)
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
        foreach (var mockable in mockablesFromTest.Where(m => m.Kind == MockableKind.Method))
        {
            // Find the method in the target type from the test compilation
            var mockableMethod = targetTypeFromTest
                .GetMembers(mockable.MemberName)
                .OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.ContainingType?.Name == mockable.ContainingTypeSimpleName);

            // If not found in the target type, search in base types
            if (mockableMethod == null)
            {
                var currentType = targetTypeFromTest.BaseType;
                while (currentType != null && currentType.SpecialType != SpecialType.System_Object)
                {
                    mockableMethod = currentType
                        .GetMembers(mockable.MemberName)
                        .OfType<IMethodSymbol>()
                        .FirstOrDefault(m => m.ContainingType?.Name == mockable.ContainingTypeSimpleName);

                    if (mockableMethod != null)
                        break;

                    currentType = currentType.BaseType;
                }
            }

            if (mockableMethod != null)
            {
                methodsToMakePublicFromTest.Add(mockableMethod);

                // Also add the base definition if this is an override
                var originalDef = mockableMethod.OriginalDefinition;
                if (mockableMethod.IsOverride && originalDef.OverriddenMethod != null)
                {
                    var baseMethod = mockableMethod.OverriddenMethod;
                    while (baseMethod?.IsOverride == true && baseMethod.OverriddenMethod != null)
                    {
                        baseMethod = baseMethod.OverriddenMethod;
                    }
                    if (baseMethod != null && !methodsToMakePublicFromTest.Contains(baseMethod, SymbolEqualityComparer.Default))
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
        var editor = await DocumentEditor.CreateAsync(document);
        var generator = editor.Generator;
        var semanticModel = await document.GetSemanticModelAsync();

        if (semanticModel == null)
        {
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

                // 1b. If MockInheritance, update base class reference
                if (instructions.MockInheritance && updatedClass.BaseList != null)
                {
                    var baseClassName = $"{instructions.OldClassName}Base_G";
                    var newBaseList = updatedClass.BaseList.WithTypes(
                        SyntaxFactory.SeparatedList(
                            [(BaseTypeSyntax)SyntaxFactory.SimpleBaseType(SyntaxFactory.IdentifierName(baseClassName))]
                        )
                    );
                    updatedClass = updatedClass.WithBaseList(newBaseList);
                }

                // 2. Add dependency injection field
                var fieldDecl = (FieldDeclarationSyntax)
                    gen.FieldDeclaration(
                        instructions.DependenciesFieldName,
                        gen.IdentifierName(instructions.DependenciesInterfaceName),
                        Accessibility.Private,
                        DeclarationModifiers.ReadOnly
                    );
                updatedClass = updatedClass.AddMembers(fieldDecl);

                // 2b. If MockInheritance, add base dependencies field
                if (instructions.MockInheritance)
                {
                    var baseDependenciesFieldName = "_baseDependencies";
                    var baseDependenciesInterfaceName = $"IAuto{instructions.OldClassName}BaseDependencies";
                    var baseDependenciesField = (FieldDeclarationSyntax)
                        gen.FieldDeclaration(
                            baseDependenciesFieldName,
                            gen.IdentifierName(baseDependenciesInterfaceName),
                            Accessibility.Private,
                            DeclarationModifiers.ReadOnly
                        );
                    updatedClass = updatedClass.AddMembers(baseDependenciesField);
                }

                // 3. Build new members list with transformed constructors and public methods
                var newMembers = new List<MemberDeclarationSyntax>();
                var hasExistingCtors = false;

                foreach (var member in updatedClass.Members)
                {
                    if (member is ConstructorDeclarationSyntax ctor)
                    {
                        hasExistingCtors = true;
                        var updatedCtor = instructions.MockInheritance
                            ? RenameAndInjectDependenciesIntoCtor(ctor, gen, instructions)
                            : RenameAndInjectDependencyIntoCtor(ctor, gen, instructions, ref needsCallerMemberName);
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
                            updatedMethod = MakeMethodPublic((MethodDeclarationSyntax)updatedMethod);
                        }

                        newMembers.Add(updatedMethod);
                    }
                    else if (
                        member is FieldDeclarationSyntax field
                        && (
                            field.Declaration.Variables.Any(v => v.Identifier.Text == instructions.DependenciesFieldName)
                            || (
                                instructions.MockInheritance
                                && field.Declaration.Variables.Any(v => v.Identifier.Text == "_baseDependencies")
                            )
                        )
                    )
                    {
                        // Keep the dependency fields we added
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
                    var defaultCtor = instructions.MockInheritance
                        ? CreateDefaultDependencyInjectionConstructorWithBase(gen, instructions)
                        : CreateDefaultDependencyInjectionConstructor(gen, instructions);
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
                    var editor3b = await DocumentEditor.CreateAsync(changedDoc);
                    var generator3b = editor3b.Generator;
                    var root3b = await changedDoc.GetSyntaxRootAsync();

                    var updatedClass = root3b
                        ?.DescendantNodes()
                        .OfType<ClassDeclarationSyntax>()
                        .FirstOrDefault(c => c.Identifier.Text == instructions.NewClassName);

                    if (updatedClass != null)
                    {
                        var baseClassName = $"{instructions.OldClassName}Base_G";
                        var baseDependenciesInterfaceName = $"IAuto{instructions.OldClassName}BaseDependencies";

                        // Generate the base class replacement
                        var baseClassDecl = BuildBaseClassReplacement(
                            generator3b,
                            instructions.OldClassName,
                            baseType,
                            usedBaseMembers,
                            baseDependenciesInterfaceName,
                            instructions.DependenciesFieldName,
                            instructions.MethodsToMakePublic
                        );

                        // Generate the base dependencies interface
                        var baseDependenciesInterfaceDecl = BuildBaseDependenciesInterface(
                            generator3b,
                            instructions.OldClassName,
                            usedBaseMembers
                        );

                        // Insert base class and interface before the derived class
                        editor3b.InsertBefore(updatedClass, baseClassDecl);
                        editor3b.InsertBefore(updatedClass, baseDependenciesInterfaceDecl);

                        changedDoc = editor3b.GetChangedDocument();

                        // If any base members have CallerMemberName attributes, we'll need the using statement
                        if (
                            usedBaseMembers
                                .OfType<IMethodSymbol>()
                                .Any(m =>
                                    m.Parameters.Any(p => p.GetAttributes().Any(a => a.AttributeClass?.Name == "CallerMemberNameAttribute"))
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

        // Apply all replacements to the tree at once using ReplaceNodes
        // This avoids issues with stale node references when applying replacements sequentially
        if (memberReplacements.Count > 0)
        {
            var replacementDict = memberReplacements.ToDictionary(x => x.oldMember, x => x.newMember);
            var newRoot = currentRoot.ReplaceNodes(replacementDict.Keys, (oldNode, _) => replacementDict[oldNode]);
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
        var replacements = new List<(SyntaxNode oldNode, SyntaxNode newNode)>();

        // FIX: Gebruik DescendantNodesAndSelf en zorg dat we diep genoeg graven (ook in accessors van properties)
        var descendantNodes = node.DescendantNodesAndSelf()
            .Where(n => n is InvocationExpressionSyntax or MemberAccessExpressionSyntax or AccessorDeclarationSyntax)
            .ToList();

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

        // Apply all replacements at once using ReplaceNodes to avoid stale node references
        if (replacements.Count > 0)
        {
            var replacementDict = replacements.ToDictionary(x => x.oldNode, x => x.newNode);
            var resultNode = node.ReplaceNodes(replacementDict.Keys, (oldNode, _) => replacementDict[oldNode]);
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
        ClassDeclarationSyntax derivedClass,
        INamedTypeSymbol targetType,
        SemanticModel semanticModel
    )
    {
        var usedMembers = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var baseType = targetType.BaseType;

        if (baseType == null || baseType.SpecialType == SpecialType.System_Object)
            return new List<ISymbol>();

        // Get all invocations and member accesses in the derived class
        var allNodes = derivedClass.DescendantNodes();

        foreach (var node in allNodes)
        {
            ISymbol? symbol = null;

            // For method calls: base.OnLoad(), OnPropertyChanged(), etc.
            if (node is InvocationExpressionSyntax invocation)
            {
                symbol = semanticModel.GetSymbolInfo(invocation.Expression).Symbol;
            }
            // For member access: base.OnLoad, this.SomeProperty, etc.
            else if (node is MemberAccessExpressionSyntax memberAccess)
            {
                symbol = semanticModel.GetSymbolInfo(memberAccess).Symbol;
            }
            // For simple identifiers: OnPropertyChanged, OnLoad (without base.), etc.
            else if (node is IdentifierNameSyntax identifier && node.Parent is not MemberAccessExpressionSyntax)
            {
                symbol = semanticModel.GetSymbolInfo(identifier).Symbol;
            }

            // Check if symbol belongs to base type
            if (symbol != null && baseType.GetMembers().Contains(symbol, SymbolEqualityComparer.Default))
            {
                usedMembers.Add(symbol);
            }
        }

        return usedMembers.ToList();
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
        return iface.WithLeadingTrivia(SyntaxFactory.TriviaList(comment));
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
        var baseDependenciesParamName = "baseDependencies";

        var members = new List<MemberDeclarationSyntax>();

        // Add constructor with base type parameter and base dependencies injection
        var ctorParams = new List<SyntaxNode>
        {
            generator.ParameterDeclaration(
                "someDataObject",
                generator.IdentifierName(baseType.Constructors.FirstOrDefault()?.Parameters.FirstOrDefault()?.Type.Name ?? "SomeDataObject")
            ),
            generator.ParameterDeclaration(baseDependenciesParamName, generator.IdentifierName(baseDependenciesInterfaceName)),
        };

        var ctorBody = new List<StatementSyntax>
        {
            SyntaxFactory.ParseStatement($"{baseDependenciesFieldName} = {baseDependenciesParamName};"),
        };

        var ctor = (ConstructorDeclarationSyntax)
            generator.ConstructorDeclaration(baseClassName, parameters: ctorParams, accessibility: Accessibility.Public);

        ctor = ctor.WithBody(SyntaxFactory.Block(ctorBody));
        members.Add(ctor);

        // Add field for base dependencies
        var depsField = (FieldDeclarationSyntax)
            generator.FieldDeclaration(
                baseDependenciesFieldName,
                generator.IdentifierName(baseDependenciesInterfaceName),
                Accessibility.Private,
                DeclarationModifiers.ReadOnly
            );
        members.Add(depsField);

        // Add empty stub implementations for used base members
        foreach (var member in usedMembers)
        {
            if (member is IMethodSymbol method && method.MethodKind != MethodKind.Constructor)
            {
                var shouldBePublic = methodsToMakePublic.Any(m => SymbolsMatch(m, method));
                var methodStub = CreateBaseMethodStub(
                    generator,
                    method,
                    baseDependenciesInterfaceName,
                    baseDependenciesFieldName,
                    shouldBePublic
                );
                members.Add(methodStub);
            }
            else if (member is IPropertySymbol property)
            {
                var propertyStub = CreateBasePropertyStub(generator, property, baseDependenciesInterfaceName, baseDependenciesFieldName);
                members.Add(propertyStub);
            }
        }

        // Create the class
        var baseClass = (ClassDeclarationSyntax)
            generator.ClassDeclaration(baseClassName, accessibility: Accessibility.Public, members: members);

        return baseClass;
    }

    private static MethodDeclarationSyntax CreateBaseMethodStub(
        SyntaxGenerator generator,
        IMethodSymbol method,
        string baseDependenciesInterfaceName,
        string baseDependenciesFieldName,
        bool shouldBePublic = false
    )
    {
        var methodName = method.Name;
        var parameters = method
            .Parameters.Select(p =>
            {
                var paramType = GetTypeNameSyntax(p.Type);
                var param = (ParameterSyntax)generator.ParameterDeclaration(p.Name, paramType);

                // Add CallerMemberName attribute if present
                if (p.GetAttributes().Any(a => a.AttributeClass?.Name == "CallerMemberNameAttribute"))
                {
                    param = generator.AddAttributes(param, generator.Attribute("CallerMemberName")) as ParameterSyntax ?? param;
                }

                // Add default value if parameter has one
                if (p.HasExplicitDefaultValue)
                {
                    var defaultValue = p.ExplicitDefaultValue;
                    var defaultExpression =
                        defaultValue == null
                            ? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
                            : (ExpressionSyntax)generator.LiteralExpression(defaultValue);
                    param = param.WithDefault(SyntaxFactory.EqualsValueClause(defaultExpression));
                }

                return param;
            })
            .ToList();

        var returnType =
            method.ReturnType.SpecialType == SpecialType.System_Void
                ? SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword))
                : GetTypeNameSyntax(method.ReturnType);

        // Build method body that calls base dependencies
        // Generate: _baseDependencies.MethodName(param1, param2, ...)
        var parameterNames = method.Parameters.Select(p => p.Name).ToList();
        var invocationArgs = parameterNames.Select(name => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(name))).ToArray();

        StatementSyntax bodyStatement;
        if (method.ReturnType.SpecialType == SpecialType.System_Void)
        {
            // For void methods: _baseDependencies.MethodName(args);
            bodyStatement = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(baseDependenciesFieldName),
                        SyntaxFactory.IdentifierName(methodName)
                    ),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(invocationArgs))
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
                    SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(invocationArgs))
                )
            );
        }

        var body = SyntaxFactory.Block(bodyStatement);

        // Determine accessibility: use public if shouldBePublic; otherwise if method is protected, use protected; otherwise public
        var accessibility = shouldBePublic
            ? Accessibility.Public
            : (method.DeclaredAccessibility == Accessibility.Protected ? Accessibility.Protected : Accessibility.Public);

        var methodDecl = (MethodDeclarationSyntax)
            generator.MethodDeclaration(
                methodName,
                parameters: parameters,
                returnType: returnType,
                accessibility: accessibility,
                modifiers: DeclarationModifiers.Virtual
            );

        return methodDecl.WithBody(body);
    }

    private static TypeSyntax GetTypeNameSyntax(ITypeSymbol type)
    {
        TypeSyntax baseSyntax;

        if (type.SpecialType == SpecialType.System_String)
            baseSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword));
        else if (type.SpecialType == SpecialType.System_Object)
            baseSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
        else if (type.SpecialType == SpecialType.System_Void)
            baseSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword));
        else if (type.SpecialType == SpecialType.System_Int32)
            baseSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword));
        else if (type.SpecialType == SpecialType.System_Boolean)
            baseSyntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword));
        else
        {
            // For all other types, use ParseTypeName to get proper TypeSyntax
            var displayName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            baseSyntax = (TypeSyntax)SyntaxFactory.ParseTypeName(displayName);
        }

        // Check if type is nullable and wrap with NullableTypeSyntax if needed
        if (type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated)
        {
            return SyntaxFactory.NullableType(baseSyntax);
        }

        return baseSyntax;
    }

    private static PropertyDeclarationSyntax CreateBasePropertyStub(
        SyntaxGenerator generator,
        IPropertySymbol property,
        string baseDependenciesInterfaceName,
        string baseDependenciesFieldName
    )
    {
        var propertyType = GetTypeNameSyntax(property.Type);
        var accessorList = new List<AccessorDeclarationSyntax>();

        if (property.GetMethod != null)
        {
            var getter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration, body: SyntaxFactory.Block());
            accessorList.Add(getter);
        }

        if (property.SetMethod != null)
        {
            var setter = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration, body: SyntaxFactory.Block());
            accessorList.Add(setter);
        }

        var propDecl = (PropertyDeclarationSyntax)
            generator.PropertyDeclaration(
                property.Name,
                propertyType,
                accessibility: Accessibility.Public,
                modifiers: DeclarationModifiers.Virtual
            );

        if (accessorList.Count > 0)
        {
            propDecl = propDecl.WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessorList)));
        }

        return propDecl;
    }

    private static SyntaxNode BuildBaseDependenciesInterface(SyntaxGenerator generator, string derivedClassName, List<ISymbol> usedMembers)
    {
        var interfaceName = $"IAuto{derivedClassName}BaseDependencies";
        var members = new List<SyntaxNode>();

        foreach (var member in usedMembers)
        {
            if (member is IMethodSymbol method && method.MethodKind != MethodKind.Constructor)
            {
                var parameters = method
                    .Parameters.Select(p =>
                    {
                        var paramType = GetTypeNameSyntax(p.Type);
                        var param = (ParameterSyntax)generator.ParameterDeclaration(p.Name, paramType);

                        // Add CallerMemberName attribute if present
                        if (p.GetAttributes().Any(a => a.AttributeClass?.Name == "CallerMemberNameAttribute"))
                        {
                            param = generator.AddAttributes(param, generator.Attribute("CallerMemberName")) as ParameterSyntax ?? param;
                        }

                        // Add default value if parameter has one
                        if (p.HasExplicitDefaultValue)
                        {
                            var defaultValue = p.ExplicitDefaultValue;
                            var defaultExpression =
                                defaultValue == null
                                    ? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
                                    : (ExpressionSyntax)generator.LiteralExpression(defaultValue);
                            param = param.WithDefault(SyntaxFactory.EqualsValueClause(defaultExpression));
                        }

                        return param;
                    })
                    .ToList();

                var returnType =
                    method.ReturnType.SpecialType == SpecialType.System_Void
                        ? SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword))
                        : GetTypeNameSyntax(method.ReturnType);

                var methodDecl = generator.MethodDeclaration(
                    method.Name,
                    parameters: parameters,
                    returnType: returnType,
                    accessibility: Accessibility.Public
                );
                members.Add(methodDecl);
            }
            else if (member is IPropertySymbol property)
            {
                var propertyType = GetTypeNameSyntax(property.Type);
                var propDecl = generator.PropertyDeclaration(
                    property.Name,
                    propertyType,
                    accessibility: Accessibility.Public,
                    getAccessorStatements: null
                );
                members.Add(propDecl);
            }
        }

        var iface = generator.InterfaceDeclaration(interfaceName, accessibility: Accessibility.Public, members: members);

        var comment = SyntaxFactory.Comment("/// <summary>Mock this using NSubstitute</summary>");
        return (iface).WithLeadingTrivia(SyntaxFactory.TriviaList(comment));
    }

    private static List<IMethodSymbol> MapMethodsToLegacy(INamedTypeSymbol legacyType, List<IMethodSymbol> methodsFromTest)
    {
        var legacyMethods = legacyType.GetMembers().OfType<IMethodSymbol>().ToList();
        var result = new List<IMethodSymbol>();

        foreach (var mb in methodsFromTest)
        {
            var match = legacyMethods.FirstOrDefault(ml => SymbolsMatch(ml, mb));
            if (match != null)
            {
                result.Add(match);
            }
            else
            {
                // If not found in the target type, try to find it in the base type
                // This handles virtual methods that need to be made public
                var baseType = legacyType.BaseType;
                while (baseType != null && baseType.SpecialType != SpecialType.System_Object)
                {
                    var baseMatch = baseType.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(ml => SymbolsMatch(ml, mb));
                    if (baseMatch != null)
                    {
                        result.Add(baseMatch);
                        break;
                    }
                    baseType = baseType.BaseType;
                }
            }
        }

        return result;
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
            return;
        }
        ExpressionSyntax? bodyExpr = lambda.Body as ExpressionSyntax;
        if (bodyExpr == null && lambda.Body is BlockSyntax block)
            bodyExpr =
                block.Statements.OfType<ReturnStatementSyntax>().Select(r => r.Expression).FirstOrDefault(e => e != null)
                as ExpressionSyntax;
        if (bodyExpr == null)
        {
            return;
        }
        var symbol = semanticModel.GetSymbolInfo(bodyExpr).Symbol;
        if (symbol == null && bodyExpr is MemberAccessExpressionSyntax mae)
            symbol = semanticModel.GetSymbolInfo(mae.Name).Symbol;
        if (symbol == null && bodyExpr is InvocationExpressionSyntax inv)
            symbol = semanticModel.GetSymbolInfo(inv.Expression).Symbol;
        if (symbol is not (IPropertySymbol or IFieldSymbol or IMethodSymbol))
        {
            return;
        }
        var spec = MockableSpec.TryCreate(symbol);
        if (spec == null)
        {
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
                var args = string.Join(", ", parameterTypes.Select(TypeDisplay).Concat([TypeDisplay(returnType)]));
                return $"global::System.Func<{args}>";
            }
        }
    }
}
