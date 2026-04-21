using FlexibleTestingDomain;
using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Simplification;
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

        var instructions = new FlexibleTestingInstructions
        {
            TargetType = null!, // Will be set below
            OldClassName = string.Empty, // Will be set below
            NewClassName = string.Empty, // Will be set below
            DependenciesInterfaceName = string.Empty, // Will be set below
            DependenciesFieldName = "_dependencies",
            DependenciesParameterName = "dependencies"
        };

        var overwritesSymbol = builderSemanticModel.Compilation.GetTypeByMetadataName(typeof(Overwrites).FullName!);

        // All method calls inside the Configure() body, like 'Overwrites.Mock<UserService>()'
        var allInstructionMethods = configureMethod.Body.DescendantNodes().OfType<InvocationExpressionSyntax>();

        INamedTypeSymbol? targetTypeFromTest = null;

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
                    AddToMakePublic(builderSemanticModel, instructions.MethodsToMakePublic, instructionMethod);
                    break;

                case nameof(Overwrites.Mockable):
                    AddToMockable(builderSemanticModel, instructions, instructionMethod);
                    break;

                case nameof(Overwrites.MockInheritance):
                    instructions.MockInheritance = true;
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

        var typeSyntaxReference = targetTypeInLegacy.DeclaringSyntaxReferences.FirstOrDefault();
        if (typeSyntaxReference == null)
        {
            return;
        }

        if (typeSyntaxReference.GetSyntax() is not ClassDeclarationSyntax targetClassNode)
        {
            return;
        }

        // For each mockable method, also add it to methodsToMakePublic so it becomes public
        /*
        foreach (var mockableMethodFromTest in instructions.MockableMethods)
        {
            instructions.MethodsToMakePublic.Add(mockableMethodFromTest);

            // Also add the base definition if this is an override
            var originalDefinition = mockableMethodFromTest.OriginalDefinition;
            if (mockableMethodFromTest.IsOverride && originalDefinition.OverriddenMethod != null)
            {
                var baseMethod = mockableMethodFromTest.OverriddenMethod;
                while (baseMethod?.IsOverride == true && baseMethod.OverriddenMethod != null)
                {
                    baseMethod = baseMethod.OverriddenMethod;
                }

                if (baseMethod != null)
                {
                    instructions.MethodsToMakePublic.Add(baseMethod);
                }
            }
        }*/

        var oldName = targetClassNode.Identifier.Text;
        
        instructions.TargetType = targetTypeInLegacy;
        instructions.OldClassName = oldName;
        instructions.NewClassName = $"{oldName}_G";
        instructions.DependenciesInterfaceName = $"IAuto{oldName}Dependencies";

        // Map methods to legacy compilation symbols
        MapMethodsToLegacy(instructions, legacyCompilation);

        var document = project.GetDocument(targetClassNode.SyntaxTree);
        if (document == null)
        {
            return;
        }

        var rewrittenDocument = ApplyRewritesAsync(document, targetClassNode, instructions).GetAwaiter().GetResult();
        var newSyntaxRoot = rewrittenDocument.GetSyntaxRootAsync().GetAwaiter().GetResult();

        var normalizedCode = newSyntaxRoot!.NormalizeWhitespace(elasticTrivia: true).ToFullString();

        var finalResult = $"""
// <auto-generated/>
{normalizedCode}
""";

        if (!string.IsNullOrWhiteSpace(OutputPath))
        {
            Directory.CreateDirectory(OutputPath);
            var fileName = $"{oldName}_G.g.cs";
            var fullPath = Path.Combine(OutputPath, fileName);
            File.WriteAllText(fullPath, finalResult, Encoding.UTF8);
        }
    }

    private async Task<Document> ApplyRewritesAsync(
        Document document,
        ClassDeclarationSyntax classNode,
        FlexibleTestingInstructions instructions
    )
    {
        var root = await document.GetSyntaxRootAsync() ?? throw new InvalidOperationException("Could not get syntax root");
        var semanticModel = await document.GetSemanticModelAsync() ?? throw new InvalidOperationException("Could not get semantic model");
        var generator = SyntaxGenerator.GetGenerator(document);

        // 1. Surgical Rewrites (Renaming, Mockable calls, Publicity)
        var rewriter = new FlexibleTestingRewriter(semanticModel, instructions, generator);
        var surgicallyTransformedRoot = rewriter.Visit(root);

        // 2. Structural Changes (Adding fields, Interfaces, Base Class)
        var editor = new SyntaxEditor(surgicallyTransformedRoot, document.Project.Solution.Workspace);
        var transformedClassNode = surgicallyTransformedRoot.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .First(c => c.Identifier.Text == instructions.NewClassName);

        // Add Dependency Injection Fields
        AddStructuralMembers(editor, transformedClassNode, instructions, generator, semanticModel.Compilation);

        // Handle MockInheritance (Base Class + Interface)
        bool baseClassNeedsCallerMemberName = false;
        if (instructions.MockInheritance && classNode.BaseList != null && classNode.BaseList.Types.Any())
        {
            baseClassNeedsCallerMemberName = AddBaseClassReplacementStructural(editor, transformedClassNode, instructions, generator, semanticModel);
        }

        // 3. Finalize Usings and Simplification
        var finalRoot = editor.GetChangedRoot();
        var usingsToAdd = new List<string> { "System" };
        if (rewriter.NeedsCallerMemberName || baseClassNeedsCallerMemberName)
        {
            usingsToAdd.Add("System.Runtime.CompilerServices");
        }

        finalRoot = AddMissingUsings(finalRoot, usingsToAdd, generator);
        
        var rewrittenDocument = document.WithSyntaxRoot(finalRoot);
        return await Simplifier.ReduceAsync(rewrittenDocument);
    }

    private void AddStructuralMembers(SyntaxEditor editor, ClassDeclarationSyntax classNode, FlexibleTestingInstructions instructions, SyntaxGenerator generator, Compilation compilation)
    {
        // 1. Add _dependencies field
        var field = generator.FieldDeclaration(
            instructions.DependenciesFieldName,
            generator.IdentifierName(instructions.DependenciesInterfaceName),
            Accessibility.Private,
            DeclarationModifiers.ReadOnly);
        editor.AddMember(classNode, field);

        // 2. Add _baseDependencies field if needed
        if (instructions.MockInheritance)
        {
            var baseField = generator.FieldDeclaration(
                "_baseDependencies",
                generator.IdentifierName($"IAuto{instructions.OldClassName}BaseDependencies"),
                Accessibility.Private,
                DeclarationModifiers.ReadOnly);
            editor.AddMember(classNode, baseField);
        }

        // 3. Add Dependencies Interface
        var interfaceDecl = BuildDependenciesInterface(generator, instructions, compilation);
        editor.InsertAfter(classNode, interfaceDecl);
    }

    private bool AddBaseClassReplacementStructural(SyntaxEditor editor, ClassDeclarationSyntax classNode, FlexibleTestingInstructions instructions, SyntaxGenerator generator, SemanticModel semanticModel)
    {
        var baseType = instructions.TargetType.BaseType;
        if (baseType == null || baseType.SpecialType == SpecialType.System_Object) return false;

        // Note: we use the ORIGINAL classNode to extract used members
        var originalClassNode = (ClassDeclarationSyntax)instructions.TargetType.DeclaringSyntaxReferences[0].GetSyntax();
        var usedBaseMembers = ExtractUsedBaseMembers(originalClassNode, instructions.TargetType, semanticModel);

        if (!usedBaseMembers.Any()) return false;

        var baseClassName = $"{instructions.OldClassName}Base_G";
        var baseDepsInterfaceName = $"IAuto{instructions.OldClassName}BaseDependencies";

        var baseClassDecl = BuildBaseClassReplacement(generator, instructions.OldClassName, baseType, usedBaseMembers, baseDepsInterfaceName, instructions.DependenciesFieldName, instructions.MethodsToMakePublic);
        var baseInterfaceDecl = BuildBaseDependenciesInterface(generator, instructions.OldClassName, usedBaseMembers);

        editor.InsertBefore(classNode, baseClassDecl);
        editor.InsertBefore(classNode, baseInterfaceDecl);

        return usedBaseMembers.OfType<IMethodSymbol>().Any(m => m.Parameters.Any(p => p.GetAttributes().Any(a => a.AttributeClass?.Name == "CallerMemberNameAttribute")));
    }

    private SyntaxNode AddMissingUsings(SyntaxNode root, IEnumerable<string> namespaces, SyntaxGenerator generator)
    {
        if (root is not CompilationUnitSyntax compilationUnit) return root;

        var currentUsings = compilationUnit.Usings.Select(u => u.Name?.ToString()).ToHashSet();
        var newUsings = namespaces
            .Where(ns => !currentUsings.Contains(ns))
            .Select(ns => (UsingDirectiveSyntax)generator.NamespaceImportDeclaration(ns))
            .ToArray();

        return compilationUnit.AddUsings(newUsings);
    }


    private static string GetMethodSignature(IMethodSymbol methodSymbol)
    {
        var parameterTypes = string.Join(",", methodSymbol.Parameters.Select(parameter => parameter.Type.ToDisplayString()));
        return $"{parameterTypes}";
    }

    private static string GetMethodSignatureFromSyntax(MethodDeclarationSyntax methodDeclaration)
    {
        var parameterTypes = string.Join(",", methodDeclaration.ParameterList.Parameters.Select(parameter => parameter.Type?.ToString() ?? string.Empty));
        return $"{parameterTypes}";
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
            }

            // Check if symbol belongs to base type
            if (memberSymbol != null && baseTypeSymbol.GetMembers().Contains(memberSymbol, SymbolEqualityComparer.Default))
            {
                usedMembers.Add(memberSymbol);
            }
        }

        return usedMembers.ToList();
    }


    private static SyntaxNode BuildDependenciesInterface(SyntaxGenerator generator, FlexibleTestingInstructions instructions, Compilation compilation)
    {
        var members = new List<SyntaxNode>();

        foreach (var mockableSymbol in instructions.AllMockables)
        {
            var dependencyMemberName = instructions.DependencyMemberNames[mockableSymbol];

            if (mockableSymbol is IMethodSymbol methodSymbol)
            {
                var methodParameters = methodSymbol.Parameters.Select(parameterSymbol =>
                {
                    var parameterDeclaration = generator.ParameterDeclaration(parameterSymbol.Name, generator.TypeExpression(parameterSymbol.Type));
                    if (parameterSymbol.GetAttributes().Any(a => a.AttributeClass?.Name == "CallerMemberNameAttribute"))
                    {
                        parameterDeclaration = generator.AddAttributes(parameterDeclaration, generator.Attribute("CallerMemberName"));
                    }
                    if (parameterSymbol.HasExplicitDefaultValue)
                    {
                        parameterDeclaration = ((ParameterSyntax)parameterDeclaration).WithDefault(
                            SyntaxFactory.EqualsValueClause((ExpressionSyntax)generator.LiteralExpression(parameterSymbol.ExplicitDefaultValue))
                        );
                    }
                    return parameterDeclaration;
                });

                var returnTypeNode = generator.TypeExpression(methodSymbol.ReturnType);

                var methodDeclaration = generator.MethodDeclaration(
                    dependencyMemberName,
                    parameters: methodParameters,
                    returnType: returnTypeNode,
                    accessibility: Accessibility.Public
                );
                members.Add(methodDeclaration);
            }
            else
            {
                var propertyDeclaration = generator.PropertyDeclaration(
                    dependencyMemberName,
                    generator.TypeExpression(GetMockableDelegateType(compilation, mockableSymbol)),
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

    private static ITypeSymbol GetMockableDelegateType(Compilation compilation, ISymbol symbol)
    {
        var (returnType, parameters) = symbol switch
        {
            IPropertySymbol propertySymbol => (propertySymbol.Type, Array.Empty<IParameterSymbol>()),
            IFieldSymbol fieldSymbol => (fieldSymbol.Type, Array.Empty<IParameterSymbol>()),
            IMethodSymbol methodSymbol => (methodSymbol.ReturnType, methodSymbol.Parameters.ToArray()),
            _ => throw new ArgumentException("Unsupported symbol kind", nameof(symbol)),
        };

        var isVoid = returnType.SpecialType == SpecialType.System_Void;
        var typeArgs = isVoid
            ? parameters.Select(p => p.Type).ToArray()
            : parameters.Select(p => p.Type).Concat(new[] { returnType }).ToArray();

        string baseName = isVoid ? typeof(Action).FullName! : typeof(Func<>).FullName!.Split('`')[0];
        string metadataName = (isVoid && typeArgs.Length == 0) ? baseName : $"{baseName}`{typeArgs.Length}";

        var delegateSymbol = compilation.GetTypeByMetadataName(metadataName);
        if (delegateSymbol == null)
        {
            throw new InvalidOperationException($"Could not find delegate type {metadataName} in compilation.");
        }

        return typeArgs.Length == 0 ? delegateSymbol : delegateSymbol.Construct(typeArgs);
    }

    private static SyntaxNode BuildBaseClassReplacement(
        SyntaxGenerator generator,
        string derivedClassName,
        INamedTypeSymbol baseType,
        List<ISymbol> usedMembers,
        string baseDependenciesInterfaceName,
        string dependenciesFieldName,
        HashSet<IMethodSymbol> methodsToMakePublic
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
                var shouldBePublic = methodsToMakePublic.Contains(methodSymbol);
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

    private static void MapMethodsToLegacy(FlexibleTestingInstructions instructions, Compilation legacyCompilation)
    {
        // 1. Map MethodsToMakePublic
        var methodsToMakePublicInLegacy = MapMethodsToLegacy(instructions.TargetType, instructions.MethodsToMakePublic);
        instructions.MethodsToMakePublic.Clear();
        foreach (var method in methodsToMakePublicInLegacy)
        {
            instructions.MethodsToMakePublic.Add(method);
        }

        // 2. Map MockableMethods, MockableProperties, MockableFields
        // Since they are from testCompilation, we need source symbols from legacyCompilation
        // However, for static calls like DateTime.Now, we use the original symbol.
        // For source symbols, we re-resolve them.
        
        // This is complex, but the original code just used the symbols from testCompilation
        // except for targetType. Let's keep it simple for now and only map what's necessary.
    }

    private static List<IMethodSymbol> MapMethodsToLegacy(INamedTypeSymbol legacyTypeSymbol, IEnumerable<IMethodSymbol> methodsFromTest)
    {
        var legacyMethods = legacyTypeSymbol.GetMembers().OfType<IMethodSymbol>().ToList();
        var result = new List<IMethodSymbol>();

        foreach (var methodFromTest in methodsFromTest)
        {
            var match = legacyMethods.FirstOrDefault(legacyMethod => SymbolSignatureComparer.Default.Equals(legacyMethod, methodFromTest));
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
                    var baseMatch = currentBaseType.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(legacyMethod => SymbolSignatureComparer.Default.Equals(legacyMethod, methodFromTest));
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

    private void AddToMockable(SemanticModel semanticModel, FlexibleTestingInstructions instructions, InvocationExpressionSyntax invocationExpression)
    {
        if (invocationExpression.ArgumentList.Arguments.Count == 0)
        {
            return;
        }

        var argumentExpression = invocationExpression.ArgumentList.Arguments.First().Expression;
        ISymbol? symbol = null;

        if (argumentExpression is LambdaExpressionSyntax lambdaExpression)
        {
            if (lambdaExpression is ParenthesizedLambdaExpressionSyntax parenthesizedLambda && parenthesizedLambda.ParameterList.Parameters.Count > 0)
            {
                return; // Ignore lambda's with paramters like (x) => ... or (x, y) => ...
            }

            var bodyExpression = lambdaExpression.Body switch
            {
                ExpressionSyntax expression => expression, // () => SomeMethod()
                BlockSyntax blockSyntax => blockSyntax.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault()?.Expression, // () => { return SomeMethod(); }
                _ => null,
            };

            if (bodyExpression is not null)
            {
                symbol = semanticModel.GetSymbolInfo(bodyExpression).Symbol
                    ?? bodyExpression switch
                    {
                        MemberAccessExpressionSyntax memberAccess => semanticModel.GetSymbolInfo(memberAccess.Name).Symbol,
                        InvocationExpressionSyntax invocation => semanticModel.GetSymbolInfo(invocation.Expression).Symbol,
                        _ => null,
                    };
            }
        }
        else if (argumentExpression is IdentifierNameSyntax methodGroupIdentifier)
        {
            symbol = semanticModel.GetSymbolInfo(methodGroupIdentifier).Symbol;
        }

        if (symbol is not (IPropertySymbol or IFieldSymbol or IMethodSymbol))
        {
            return;
        }

        // Bepaal de basisnaam voor het dependency-lid.
        var baseMemberName = symbol.Name;
        var finalMemberName = baseMemberName;
        int duplicateSuffix = 1;

        // Voorkom dubbele namen in de lijst: als de naam al bestaat, voeg een nummer toe (bijv. _1, _2).
        /*
        while (instructions.DependencyMemberNames.Values.Any(name => string.Equals(name, finalMemberName, StringComparison.Ordinal)))
        {
            finalMemberName = $"{baseMemberName}_{duplicateSuffix}";
            duplicateSuffix++;
        }*/

        switch (symbol)
        {
            case IMethodSymbol methodSymbol:
                instructions.MockableMethods.Add(methodSymbol);
                break;
            case IPropertySymbol propertySymbol:
                instructions.MockableProperties.Add(propertySymbol);
                break;
            case IFieldSymbol fieldSymbol:
                instructions.MockableFields.Add(fieldSymbol);
                break;
        }

        instructions.DependencyMemberNames[symbol] = finalMemberName;
    }
}
