using FlexibleTestingDomain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
                        yield return instructions;
                }
            }
        }
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
                case nameof(Overwrites.Mockable):
                    AddToMockable(model, instructions, instructionMethod);
                    break;
                case nameof(Overwrites.MockInheritance):
                    instructions.MockInheritance = true;
                    break;
            }
        }

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

        return instructions;
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

    private void AddToMockable(SemanticModel model, FlexibleTestingInstructions instructions, InvocationExpressionSyntax invocation)
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
            instructions.MockableMethods.Add(m);
        else if (symbol is IPropertySymbol p)
            instructions.MockableProperties.Add(p);
        else if (symbol is IFieldSymbol f)
            instructions.MockableFields.Add(f);

        if (symbol != null)
            instructions.DependencyMemberNames[symbol] = symbol.Name;
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
