using FlexibleTestingDomain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FlexibleTesting.Tasks;

public class FlexibleTestingInstructionsCreator
{
    private readonly Compilation _legacyCompilation;
    private readonly Compilation _testCompilation;
    private readonly INamedTypeSymbol? _targetAttributeSymbol;
    private readonly INamedTypeSymbol? _overwritesSymbol;

    public FlexibleTestingInstructionsCreator(Compilation legacyCompilation, Compilation testCompilation)
    {
        _legacyCompilation = legacyCompilation;
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
            DependenciesFieldName = "_dependencies",
            DependenciesParameterName = "dependencies",
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
                    targetTypeFromTest = methodSymbol.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
                    break;
                case nameof(Overwrites.MakePublic):
                    AddToMakePublic(model, instructions.MethodsToMakePublic, instructionMethod);
                    break;
                case nameof(Overwrites.Mock):
                    AddClassMock(instructions, methodSymbol, instructionMethod);
                    AddToMock(model, instructions, instructionMethod);
                    break;
                case nameof(Overwrites.MockInheritance):
                    instructions.MockInheritance = true;
                    break;
                case nameof(Overwrites.RecursiveMockInheritance):
                    instructions.RecursiveMockInheritance = true;
                    break;
            }
        }

        if (instructions.MockInheritance && instructions.RecursiveMockInheritance)
            throw new InvalidOperationException(
                $"Builder '{classNode.Identifier.ValueText}' cannot use both MockInheritance() and RecursiveMockInheritance()."
            );

        if (targetTypeFromTest == null || targetTypeFromTest.TypeKind == TypeKind.Error)
            return null;

        var targetMetadataName = GetTypeMetadataName(targetTypeFromTest.OriginalDefinition);
        var targetTypeInLegacy = _legacyCompilation.GetTypeByMetadataName(targetMetadataName);

        if (targetTypeInLegacy?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not ClassDeclarationSyntax targetClassNode)
            return null;

        var oldName = targetClassNode.Identifier.Text;
        instructions.TargetType = targetTypeInLegacy;
        instructions.OldClassName = oldName;
        instructions.NewClassName = $"{oldName}_G";
        instructions.DependenciesInterfaceName = $"IAuto{oldName}Dependencies";

        MapMethodsToLegacy(instructions);
        MapMockClassConstructors(instructions, targetClassNode);

        return instructions;
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
            ValidateRecursiveChain(instructions, byTargetMetadataName, new HashSet<string>(StringComparer.Ordinal));
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
            throw new InvalidOperationException(
                $"Recursive inheritance cycle detected while resolving '{instructions.TargetType.Name}'."
            );
        }

        if (!byTargetMetadataName.TryGetValue(baseMetadataName, out var baseInstructions))
        {
            throw new InvalidOperationException(
                $"RecursiveMockInheritance() on '{instructions.TargetType.Name}' requires a builder for its base type '{baseType.Name}'."
            );
        }

        if (!baseInstructions.RecursiveMockInheritance && baseType.BaseType is { SpecialType: not SpecialType.System_Object } )
        {
            throw new InvalidOperationException(
                $"Builder '{baseInstructions.TargetType.Name}' must also call RecursiveMockInheritance() because it has a non-object base type."
            );
        }

        ValidateRecursiveChain(baseInstructions, byTargetMetadataName, traversalStack);
        traversalStack.Remove(baseMetadataName);
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

    private void AddToMock(SemanticModel model, FlexibleTestingInstructions instructions, InvocationExpressionSyntax invocation)
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

        if (symbol is IMethodSymbol m)
            instructions.MockMethods.Add(m);
        else if (symbol is IPropertySymbol p)
            instructions.MockProperties.Add(p);
        else if (symbol is IFieldSymbol f)
            instructions.MockFields.Add(f);
        else if (symbol is INamedTypeSymbol namedType && namedType.TypeKind == TypeKind.Class)
        {
            instructions.MockClasses.Add(namedType);
            instructions.DependencyMemberNames[namedType] = namedType.Name;
        }

        if (symbol != null)
            instructions.DependencyMemberNames[symbol] = symbol.Name;
    }

    private void AddClassMock(
        FlexibleTestingInstructions instructions,
        IMethodSymbol methodSymbol,
        InvocationExpressionSyntax invocation
    )
    {
        if (invocation.ArgumentList.Arguments.Count > 0)
            return;

        if (methodSymbol.TypeArguments.FirstOrDefault() is not INamedTypeSymbol mockType || mockType.TypeKind != TypeKind.Class)
            return;

        var legacyMockType = _legacyCompilation.GetTypeByMetadataName(GetTypeMetadataName(mockType));
        if (legacyMockType == null)
            return;

        instructions.MockClasses.Add(legacyMockType);
        instructions.DependencyMemberNames[legacyMockType] = legacyMockType.Name;
    }

    private void MapMockClassConstructors(
        FlexibleTestingInstructions instructions,
        ClassDeclarationSyntax targetClassNode
    )
    {
        var mockedTypes = instructions.MockClasses;
        if (!mockedTypes.Any())
            return;

        var legacyModel = _legacyCompilation.GetSemanticModel(targetClassNode.SyntaxTree);

        foreach (var creation in targetClassNode.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (legacyModel.GetSymbolInfo(creation).Symbol is not IMethodSymbol ctor)
                continue;

            if (mockedTypes.Any(t => SymbolEqualityComparer.Default.Equals(t, ctor.ContainingType)))
                instructions.MockClassConstructors.Add(ctor);
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
