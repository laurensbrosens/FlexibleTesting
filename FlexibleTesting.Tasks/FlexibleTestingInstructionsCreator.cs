using FlexibleTestingDomain;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FlexibleTesting.Tasks;

public class FlexibleTestingInstructionsCreator
{
    private readonly Solution _solution;
    private readonly Compilation _testCompilation;
    private readonly INamedTypeSymbol? _targetAttributeSymbol;
    private readonly INamedTypeSymbol? _overwritesSymbol;

    public FlexibleTestingInstructionsCreator(Solution solution, Compilation testCompilation)
    {
        _solution = solution;
        _testCompilation = testCompilation;
        _targetAttributeSymbol = _testCompilation.GetTypeByMetadataName(typeof(GeneratorInstructionsAttribute).FullName!);
        _overwritesSymbol = _testCompilation.GetTypeByMetadataName(typeof(Overwrites).FullName!);
    }

    public IEnumerable<FlexibleTestingInstructions> CreateAll()
    {
        var createdInstructions = new List<FlexibleTestingInstructions>();

        foreach (var tree in _testCompilation.SyntaxTrees)
        {
            var model = _testCompilation.GetSemanticModel(tree);
            var classes = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>();

            foreach (var classNode in classes)
            {
                if (IsTargetBuilder(classNode, model))
                {
                    var instructions = Create(classNode, model);
                    if (instructions != null)
                        createdInstructions.Add(instructions);
                }
            }
        }

        ValidateRecursiveInheritance(createdInstructions);

        foreach (var instructions in createdInstructions)
            yield return instructions;
    }

    private bool IsTargetBuilder(ClassDeclarationSyntax classNode, SemanticModel model)
    {
        var symbol = model.GetDeclaredSymbol(classNode);
        return symbol?.GetAttributes().Any(a => a.AttributeClass?.IsEqualToSymbol(_targetAttributeSymbol) ?? false) ?? false;
    }

    public FlexibleTestingInstructions? Create(ClassDeclarationSyntax classNode, SemanticModel model)
    {
        var configureMethod = classNode
            .Members.OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.ValueText == nameof(IGeneratorInstructions.Configure));

        if (configureMethod?.Body == null)
            return null;

        var instructions = new FlexibleTestingInstructions
        {
            DependenciesFieldName = FlexibleTestingGeneratedNames.DependenciesFieldName,
            DependenciesParameterName = FlexibleTestingGeneratedNames.DependenciesParameterName,
        };

        var allInstructionMethods = configureMethod.Body.DescendantNodes().OfType<InvocationExpressionSyntax>();
        INamedTypeSymbol? targetTypeFromTest = null;

        foreach (var instructionMethod in allInstructionMethods)
        {
            if (
                model.GetSymbolInfo(instructionMethod).Symbol is not IMethodSymbol methodSymbol
                || !methodSymbol.IsDeclaredIn(_overwritesSymbol)
            )
                continue;

            switch (methodSymbol.Name)
            {
                case nameof(Overwrites.ForClass):
                    if (methodSymbol.IsGenericMethod)
                    {
                        targetTypeFromTest = methodSymbol.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
                    }
                    else
                    {
                        var arg = instructionMethod.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                        if (arg is TypeOfExpressionSyntax typeOfExpression)
                        {
                            targetTypeFromTest = model.GetSymbolInfo(typeOfExpression.Type).Symbol as INamedTypeSymbol;
                        }
                    }
                    break;
                case nameof(Overwrites.MakePublic):
                    AddToMakePublic(model, instructions.MethodsToMakePublic, instructionMethod);
                    break;
                case nameof(Overwrites.Mock):
                    AddClassMock(instructions, methodSymbol, instructionMethod);
                    AddToMock(model, instructions, instructionMethod, useSignature: false);
                    break;
                case nameof(Overwrites.Include):
                    if (methodSymbol.IsGenericMethod)
                    {
                        var includedType = methodSymbol.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
                        if (includedType != null) instructions.IncludedBuilders.Add(includedType);
                    }
                    break;
                case nameof(Overwrites.MockInheritance):
                    instructions.MockInheritance = true;
                    break;
                case nameof(Overwrites.RecursiveMockInheritance):
                    instructions.RecursiveMockInheritance = true;
                    break;
                case nameof(Overwrites.RemoveSealed):
                    instructions.RemoveSealed = true;
                    break;
            }
        }

        if (instructions.MockInheritance && instructions.RecursiveMockInheritance)
            throw new InvalidOperationException(
                $"Builder '{classNode.Identifier.ValueText}' cannot use both MockInheritance() and RecursiveMockInheritance()."
            );

        if (targetTypeFromTest == null || targetTypeFromTest.TypeKind == TypeKind.Error)
            return null;

        var syntaxRefs = targetTypeFromTest.DeclaringSyntaxReferences;
        if (syntaxRefs.Any())
        {
            foreach (var syntaxRef in syntaxRefs)
            {
                if (syntaxRef.GetSyntax() is ClassDeclarationSyntax targetClassNode)
                {
                    var targetDocument = _solution.GetDocument(targetClassNode.SyntaxTree);
                    if (targetDocument != null)
                    {
                        instructions.Parts.Add(new FlexibleTestingPart { Document = targetDocument, Syntax = targetClassNode });
                    }
                }
            }
        }
        else
        {
            var decompilationResult = TryDecompile(targetTypeFromTest);
            if (decompilationResult.node != null && decompilationResult.doc != null)
            {
                instructions.Parts.Add(new FlexibleTestingPart { Document = decompilationResult.doc, Syntax = decompilationResult.node });
            }
        }

        if (!instructions.Parts.Any())
            return null;

        var mainPart = instructions.Parts[0];
        var targetDocumentForCompilation = mainPart.Document;
        var targetClassNodeForName = mainPart.Syntax;

        var targetCompilation = targetDocumentForCompilation.Project.GetCompilationAsync().Result;
        if (targetCompilation == null)
            return null;

        var targetMetadataName = GetTypeMetadataName(targetTypeFromTest.OriginalDefinition);
        var targetTypeInLegacy = targetCompilation.GetTypeByMetadataName(targetMetadataName);
        if (targetTypeInLegacy == null)
            return null;

        if (instructions.MockClasses.Count > 0)
        {
            var resolvedMockClasses = new HashSet<INamedTypeSymbol>(SymbolSignatureComparer.Default);
            foreach (var mockType in instructions.MockClasses)
            {
                var resolvedMockType = targetCompilation.GetTypeByMetadataName(GetTypeMetadataName(mockType));
                if (resolvedMockType != null)
                    resolvedMockClasses.Add(resolvedMockType);
            }

            instructions.MockClasses.Clear();
            foreach (var resolvedMockType in resolvedMockClasses)
                instructions.MockClasses.Add(resolvedMockType);
        }

        var oldName = targetClassNodeForName.Identifier.Text;
        instructions.TargetType = targetTypeInLegacy;
        instructions.OldClassName = oldName;
        instructions.NewClassName = FlexibleTestingGeneratedNames.GetGeneratedClassName(oldName);
        instructions.DependenciesInterfaceName = FlexibleTestingGeneratedNames.GetDependenciesInterfaceName(oldName);
        instructions.IsPartial = instructions.Parts.Count > 1 || mainPart.Syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));

        MapMethodsToLegacy(instructions);
        MapMockClassConstructors(instructions, instructions.Parts.Select(p => p.Syntax), targetDocumentForCompilation);
        
        MergeIncludedInstructions(instructions);
        NormalizeDependencyMemberNames(instructions);

        return instructions;
    }

    private static void NormalizeDependencyMemberNames(FlexibleTestingInstructions instructions)
    {
        foreach (var group in instructions.DependencyMemberNames.Keys.GroupBy(GetSimpleDependencyMemberName, StringComparer.Ordinal))
        {
            var dependencyMemberName = group.Count() == 1 ? group.Key : null;

            foreach (var symbol in group)
            {
                instructions.DependencyMemberNames[symbol] = dependencyMemberName
                    ?? GetQualifiedDependencyMemberName(symbol);
            }
        }
    }

    private static string GetSimpleDependencyMemberName(ISymbol symbol)
    {
        if (symbol is INamedTypeSymbol namedType)
            return namedType.Name;

        if (symbol.IsStatic && symbol.ContainingType != null)
            return $"{symbol.ContainingType.Name}_{symbol.Name}";

        return symbol.Name;
    }

    private static string GetQualifiedDependencyMemberName(ISymbol symbol)
    {
        var symbolPath = symbol is INamedTypeSymbol namedType
            ? namedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : $"{symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}_{symbol.Name}";

        return ToIdentifier(symbolPath);
    }

    private static string ToIdentifier(string value)
    {
        var identifier = value.Replace("global::", string.Empty);
        var characters = identifier.Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_').ToArray();
        var result = new string(characters);

        if (string.IsNullOrEmpty(result))
            return "Dependency";

        return char.IsLetter(result[0]) || result[0] == '_' ? result : $"_{result}";
    }

    private void MergeIncludedInstructions(FlexibleTestingInstructions instructions)
    {
        var visited = new HashSet<INamedTypeSymbol>(SymbolSignatureComparer.Default);
        var queue = new Queue<INamedTypeSymbol>(instructions.IncludedBuilders);

        while (queue.Count > 0)
        {
            var builderType = queue.Dequeue();
            if (!visited.Add(builderType)) continue;

            var syntaxRef = builderType.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef?.GetSyntax() is ClassDeclarationSyntax builderNode)
            {
                var doc = _solution.GetDocument(builderNode.SyntaxTree);
                if (doc != null)
                {
                    var model = doc.GetSemanticModelAsync().Result;
                    if (model != null)
                    {
                        var includedInstructions = Create(builderNode, model);
                        if (includedInstructions != null)
                        {
                            foreach (var method in includedInstructions.MockMethods) instructions.MockMethods.Add(method);
                            foreach (var prop in includedInstructions.MockProperties) instructions.MockProperties.Add(prop);
                            foreach (var field in includedInstructions.MockFields) instructions.MockFields.Add(field);
                            foreach (var cls in includedInstructions.MockClasses) instructions.MockClasses.Add(cls);
                            foreach (var kvp in includedInstructions.DependencyMemberNames) instructions.DependencyMemberNames[kvp.Key] = kvp.Value;
                            foreach (var included in includedInstructions.IncludedBuilders) queue.Enqueue(included);
                        }
                    }
                }
            }
        }
    }

    private (ClassDeclarationSyntax? node, Document? doc) TryDecompile(INamedTypeSymbol symbol)
    {
        string? rawPath = GetRuntimeDllPath(symbol);
        if (string.IsNullOrEmpty(rawPath))
            return (null, null);

        string fixedPath = Path.GetFullPath(rawPath);
        if (!File.Exists(fixedPath))
            return (null, null);

        var decompiler = new CSharpDecompiler(fixedPath, new DecompilerSettings());
        var fullTypeName = GetFullTypeName(symbol);

        var typeDef = decompiler.TypeSystem.MainModule.GetTypeDefinition(fullTypeName.TopLevelTypeName);
        if (typeDef == null)
            return (null, null);

        string code = decompiler.DecompileTypeAsString(fullTypeName);

        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);

        var solutionWithDoc = _solution
            .AddProject(projectId, $"Decompiled_{symbol.Name}", $"Decompiled_{symbol.Name}", LanguageNames.CSharp)
            .AddDocument(docId, $"{symbol.Name}_Decompiled.cs", code);

        var virtualDoc = solutionWithDoc.GetDocument(docId);
        var root = virtualDoc?.GetSyntaxRootAsync().GetAwaiter().GetResult();

        var node = root?.DescendantNodesAndSelf()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(n => n.Identifier.ValueText == symbol.Name);

        return (node, virtualDoc);
    }

    private string? GetRuntimeDllPath(INamedTypeSymbol symbol)
    {
        var assemblyReference = _testCompilation.GetMetadataReference(symbol.ContainingAssembly) as PortableExecutableReference;
        if (string.IsNullOrEmpty(assemblyReference?.FilePath))
            return null;

        var path = assemblyReference?.FilePath;
        string refDir = $"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}";
        string libDir = $"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}";

        if (path?.Contains(refDir, StringComparison.OrdinalIgnoreCase) == false)
        {
            path = path?.Replace(refDir, libDir)!;
        }

        return path;
    }

    private ICSharpCode.Decompiler.TypeSystem.FullTypeName GetFullTypeName(INamedTypeSymbol symbol)
    {
        var format = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.None
        );

        return new ICSharpCode.Decompiler.TypeSystem.FullTypeName(symbol.ToDisplayString(format));
    }

    private void ValidateRecursiveInheritance(IReadOnlyList<FlexibleTestingInstructions> instructionsList)
    {
        var byTargetMetadataName = new Dictionary<string, FlexibleTestingInstructions>(StringComparer.Ordinal);

        foreach (var instructions in instructionsList)
        {
            var metadataName = GetTypeMetadataName(instructions.TargetType.OriginalDefinition);
            if (byTargetMetadataName.ContainsKey(metadataName))
            {
                throw new InvalidOperationException(
                    $"Multiple generator builders target '{metadataName}'. Recursive inheritance requires one builder per target type."
                );
            }

            byTargetMetadataName[metadataName] = instructions;
        }

        foreach (var instructions in instructionsList.Where(i => i.RecursiveMockInheritance))
        {
            ValidateRecursiveChain(instructions, byTargetMetadataName, new HashSet<string>(StringComparer.Ordinal));
            PopulateRecursiveBaseTypes(instructions, byTargetMetadataName);
        }
    }

    private void ValidateRecursiveChain(
        FlexibleTestingInstructions instructions,
        IReadOnlyDictionary<string, FlexibleTestingInstructions> byTargetMetadataName,
        HashSet<string> traversalStack
    )
    {
        var baseType = instructions.TargetType.BaseType;
        if (baseType == null || baseType.SpecialType == SpecialType.System_Object)
            return;

        var baseMetadataName = GetTypeMetadataName(baseType.OriginalDefinition);
        if (!traversalStack.Add(baseMetadataName))
        {
            throw new InvalidOperationException($"Recursive inheritance cycle detected while resolving '{instructions.TargetType.Name}'.");
        }

        if (!byTargetMetadataName.TryGetValue(baseMetadataName, out var baseInstructions))
        {
            throw new InvalidOperationException(
                $"RecursiveMockInheritance() on '{instructions.TargetType.Name}' requires a builder for its base type '{baseType.Name}'."
            );
        }

        if (!baseInstructions.RecursiveMockInheritance && baseType.BaseType is { SpecialType: not SpecialType.System_Object })
        {
            throw new InvalidOperationException(
                $"Builder '{baseInstructions.TargetType.Name}' must also call RecursiveMockInheritance() because it has a non-object base type."
            );
        }

        ValidateRecursiveChain(baseInstructions, byTargetMetadataName, traversalStack);
        traversalStack.Remove(baseMetadataName);
    }

    private void PopulateRecursiveBaseTypes(
        FlexibleTestingInstructions instructions,
        IReadOnlyDictionary<string, FlexibleTestingInstructions> byTargetMetadataName
    )
    {
        instructions.RecursiveBaseTypes.Clear();

        var currentBaseType = instructions.TargetType.BaseType;
        while (currentBaseType != null && currentBaseType.SpecialType != SpecialType.System_Object)
        {
            var currentMetadataName = GetTypeMetadataName(currentBaseType.OriginalDefinition);
            if (!byTargetMetadataName.TryGetValue(currentMetadataName, out var currentBaseInstructions))
                break;

            instructions.RecursiveBaseTypes.Add(currentBaseInstructions.TargetType);
            currentBaseType = currentBaseType.BaseType;
        }
    }

    private void AddToMakePublic(SemanticModel model, HashSet<IMethodSymbol> methodsToMakePublic, InvocationExpressionSyntax invocation)
    {
        var arg = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        if (arg == null)
            return;

        var nodeToInspect = arg is LambdaExpressionSyntax lambda
            ? (lambda.Body is InvocationExpressionSyntax body ? body.Expression : lambda.Body)
            : arg;

        if (model.GetSymbolInfo(nodeToInspect).Symbol is IMethodSymbol method)
        {
            methodsToMakePublic.Add(method);
        }
    }

    private void AddToMock(SemanticModel model, FlexibleTestingInstructions instructions, InvocationExpressionSyntax invocation, bool useSignature)
    {
        var arg = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        ISymbol? symbol = null;

        if (arg is LambdaExpressionSyntax lambda)
        {
            if (lambda is ParenthesizedLambdaExpressionSyntax p && p.ParameterList.Parameters.Count > 0)
                return;
            var body = lambda.Body switch
            {
                ExpressionSyntax e => e,
                BlockSyntax b => b.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault()?.Expression,
                _ => null,
            };
            if (body != null)
                symbol =
                    model.GetSymbolInfo(body).Symbol
                    ?? (
                        body switch
                        {
                            MemberAccessExpressionSyntax ma => model.GetSymbolInfo(ma.Name).Symbol,
                            InvocationExpressionSyntax i => model.GetSymbolInfo(i.Expression).Symbol,
                            _ => null,
                        }
                    );
        }
        else if (arg is IdentifierNameSyntax id)
            symbol = model.GetSymbolInfo(id).Symbol;

        if (symbol == null) return;

        if (useSignature)
        {
            if (symbol is IMethodSymbol m) instructions.MockMethodsSignature.Add(m);
            else if (symbol is IPropertySymbol p) instructions.MockPropertiesSignature.Add(p);
            else if (symbol is IFieldSymbol f) instructions.MockFieldsSignature.Add(f);
        }
        else
        {
            if (symbol is IMethodSymbol m) instructions.MockMethods.Add(m);
            else if (symbol is IPropertySymbol p) instructions.MockProperties.Add(p);
            else if (symbol is IFieldSymbol f) instructions.MockFields.Add(f);
            else if (symbol is INamedTypeSymbol namedType && namedType.TypeKind == TypeKind.Class)
            {
                instructions.MockClasses.Add(namedType);
                instructions.DependencyMemberNames[namedType] = namedType.Name;
            }
        }

        instructions.DependencyMemberNames[symbol] = GetSimpleDependencyMemberName(symbol);
    }

    private void AddClassMock(FlexibleTestingInstructions instructions, IMethodSymbol methodSymbol, InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList.Arguments.Count > 0)
            return;

        if (methodSymbol.TypeArguments.FirstOrDefault() is not INamedTypeSymbol mockType || mockType.TypeKind != TypeKind.Class)
            return;

        var legacyMockType = mockType;
        if (legacyMockType == null)
            return;

        instructions.MockClasses.Add(legacyMockType);
        instructions.DependencyMemberNames[legacyMockType] = GetSimpleDependencyMemberName(legacyMockType);
    }

    private void MapMockClassConstructors(
        FlexibleTestingInstructions instructions,
        IEnumerable<ClassDeclarationSyntax> targetClassNodes,
        Document targetDocument
    )
    {
        var mockedTypes = instructions.MockClasses;
        if (!mockedTypes.Any())
            return;

        var legacyModel = targetDocument.GetSemanticModelAsync().Result;
        if (legacyModel == null)
            return;

        foreach (var targetClassNode in targetClassNodes)
        {
            foreach (var creation in targetClassNode.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                if (legacyModel.GetSymbolInfo(creation).Symbol is not IMethodSymbol ctor)
                    continue;

                if (mockedTypes.Any(t => SymbolSignatureComparer.Default.Equals(t, ctor.ContainingType)))
                    instructions.MockClassConstructors.Add(ctor);
            }
        }
    }

    private void MapMethodsToLegacy(FlexibleTestingInstructions instructions)
    {
        var mappedMethods = MapMethodsToLegacy(instructions.TargetType, instructions.MethodsToMakePublic);
        instructions.MethodsToMakePublic.Clear();
        foreach (var m in mappedMethods)
            instructions.MethodsToMakePublic.Add(m);
    }

    private List<IMethodSymbol> MapMethodsToLegacy(INamedTypeSymbol type, IEnumerable<IMethodSymbol> methods)
    {
        var result = new List<IMethodSymbol>();
        var legacyMembers = type.GetMembers().OfType<IMethodSymbol>().ToList();

        foreach (var m in methods)
        {
            var match = legacyMembers.FirstOrDefault(lm => SymbolSignatureComparer.Default.Equals(lm, m));
            if (match != null)
                result.Add(match);
            else
            {
                var currentBase = type.BaseType;
                while (currentBase != null && currentBase.SpecialType != SpecialType.System_Object)
                {
                    var baseMatch = currentBase
                        .GetMembers()
                        .OfType<IMethodSymbol>()
                        .FirstOrDefault(lm => SymbolSignatureComparer.Default.Equals(lm, m));
                    if (baseMatch != null)
                    {
                        result.Add(baseMatch);
                        break;
                    }
                    currentBase = currentBase.BaseType;
                }
            }
        }
        return result;
    }

    private string GetTypeMetadataName(INamedTypeSymbol type)
    {
        var parts = new Stack<string>();
        for (var curr = type.OriginalDefinition; curr != null; curr = curr.ContainingType)
            parts.Push(curr.MetadataName);
        var typeName = string.Join("+", parts);
        var ns = type.OriginalDefinition.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() : null;
        return string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";
    }
}
