using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Legacy.Tests.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class LegacyTestableSutGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var overwriteRules = context
            .SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax,
                static (ctx, ct) => RuleExtraction.TryExtractOverwrite(ctx, ct)
            )
            .Where(static r => r is not null)
            .Select(static (r, _) => r!)
            .Collect();

        var fakeBaseRules = context
            .SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax,
                static (ctx, ct) => RuleExtraction.TryExtractFakeBase(ctx, ct)
            )
            .Where(static r => r is not null)
            .Select(static (r, _) => r!)
            .Collect();

        var sutInputs = context
            .AdditionalTextsProvider.Where(static at =>
                at.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            )
            .Select(static (at, ct) => new SutInput(at.Path, at.GetText(ct)));

        context.RegisterSourceOutput(
            context
                .CompilationProvider.Combine(sutInputs.Collect())
                .Combine(overwriteRules)
                .Combine(fakeBaseRules),
            static (spc, data) =>
            {
                var ((compilation, sutFiles), overwrites) = data.Left;
                var fakeBases = data.Right;

                foreach (var sut in sutFiles)
                {
                    if (sut.Text is null)
                        continue;

                    var fileName = Path.GetFileName(sut.Path);
                    if (
                        !string.Equals(
                            fileName,
                            "CustomerViewModel.cs",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        continue;

                    GenerateCustomerViewModelTestClass(
                        spc,
                        compilation,
                        sut.Text,
                        overwrites,
                        fakeBases
                    );
                }
            }
        );
    }

    private static void GenerateCustomerViewModelTestClass(
        SourceProductionContext spc,
        Compilation compilation,
        SourceText sutSource,
        ImmutableArray<CallOverwriteRule> overwrites,
        ImmutableArray<FakeBaseRule> fakeBases
    )
    {
        var parseOptions =
            compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions
            ?? new CSharpParseOptions(LanguageVersion.Preview);

        var sutTree = CSharpSyntaxTree.ParseText(
            sutSource,
            parseOptions,
            cancellationToken: spc.CancellationToken
        );
        var compilationWithSut = compilation.AddSyntaxTrees(sutTree);
        var model = compilationWithSut.GetSemanticModel(sutTree, ignoreAccessibility: true);

        var root = sutTree.GetCompilationUnitRoot(spc.CancellationToken);
        var originalClass = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == "CustomerViewModel");

        if (originalClass is null)
            return;

        var rewriter = new TestableSutRewriter(
            model,
            overwrites,
            fakeBases,
            "CustomerViewModel",
            "CustomerViewModel_TestClass"
        );

        var rewrittenNode = rewriter.Visit(root);
        if (rewrittenNode is not CompilationUnitSyntax rewrittenRoot)
            return;

        var generatedClass = rewrittenRoot
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == "CustomerViewModel_TestClass");

        if (generatedClass is null)
            return;

        var originalUsings = root.Usings;

        var extraUsings = new[]
        {
            SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Collections.Generic")),
            SyntaxFactory.UsingDirective(
                SyntaxFactory.ParseName("System.Runtime.CompilerServices")
            ),
            SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("Legacy.App")),
        };

        var allUsings = originalUsings
            .Concat(extraUsings)
            .GroupBy(u => u.Name?.ToString() ?? "")
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .Select(g => g.First())
            .ToImmutableArray();

        var ns = SyntaxFactory
            .FileScopedNamespaceDeclaration(SyntaxFactory.ParseName("Legacy.Tests.SutCopy"))
            .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(generatedClass));

        var output = SyntaxFactory
            .CompilationUnit()
            .WithUsings(SyntaxFactory.List(allUsings))
            .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(ns))
            .NormalizeWhitespace();

        spc.AddSource("CustomerViewModel_TestClass.g.cs", output.GetText());
    }

    private sealed record SutInput(string Path, SourceText? Text);
}

internal sealed class TestableSutRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly ImmutableArray<CallOverwriteRule> _overwrites;
    private readonly ImmutableArray<FakeBaseRule> _fakeBases;
    private readonly string _oldClassName;
    private readonly string _newClassName;

    public TestableSutRewriter(
        SemanticModel model,
        ImmutableArray<CallOverwriteRule> overwrites,
        ImmutableArray<FakeBaseRule> fakeBases,
        string oldClassName,
        string newClassName
    )
    {
        _model = model;
        _overwrites = overwrites;
        _fakeBases = fakeBases;
        _oldClassName = oldClassName;
        _newClassName = newClassName;
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var updated = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;

        if (node.Identifier.Text != _oldClassName)
            return updated;

        updated = updated.WithIdentifier(SyntaxFactory.Identifier(_newClassName));

        updated = RewriteBaseList(updated);

        if (!HasSetProperty(updated))
            updated = updated.WithMembers(updated.Members.Add(CreateSetPropertyMember()));

        return updated;
    }

    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var updated = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;

        if (node.Identifier.Text == _oldClassName)
            updated = updated.WithIdentifier(SyntaxFactory.Identifier(_newClassName));

        return updated;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var updated = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

        var symbol = _model.GetSymbolInfo(node, CancellationToken.None).Symbol as IMethodSymbol;
        if (symbol is null)
            return updated;

        var rule = _overwrites.FirstOrDefault(r =>
            SymbolEqualityComparer.Default.Equals(r.Target, symbol.OriginalDefinition)
        );
        if (rule is null)
            return updated;

        if (rule.Replacement is not IMethodSymbol replacementMethod)
            return updated;

        var expr = node.Expression;

        if (symbol.IsExtensionMethod && expr is MemberAccessExpressionSyntax extensionMemberAccess)
        {
            var receiver = (ExpressionSyntax)Visit(extensionMemberAccess.Expression)!;

            var newArgs = new List<ArgumentSyntax> { SyntaxFactory.Argument(receiver) };
            newArgs.AddRange(updated.ArgumentList.Arguments);

            return SyntaxFactory.InvocationExpression(
                CreateStaticMethodAccess(replacementMethod),
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(newArgs))
            );
        }

        if (expr is MemberAccessExpressionSyntax memberAccess)
        {
            var receiver = (ExpressionSyntax)Visit(memberAccess.Expression)!;

            ExpressionSyntax newCallee = replacementMethod.IsStatic
                ? CreateStaticMethodAccess(replacementMethod)
                : SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    receiver,
                    SyntaxFactory.IdentifierName(replacementMethod.Name)
                );

            return SyntaxFactory.InvocationExpression(newCallee, updated.ArgumentList);
        }

        if (expr is IdentifierNameSyntax && replacementMethod.IsStatic)
        {
            return SyntaxFactory.InvocationExpression(
                CreateStaticMethodAccess(replacementMethod),
                updated.ArgumentList
            );
        }

        return updated;
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var updated = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;

        var symbol = _model.GetSymbolInfo(node, CancellationToken.None).Symbol as IPropertySymbol;
        if (symbol is null)
            return updated;

        var rule = _overwrites.FirstOrDefault(r =>
            SymbolEqualityComparer.Default.Equals(r.Target, symbol.OriginalDefinition)
        );
        if (rule is null)
            return updated;

        if (rule.Replacement is not IPropertySymbol replacementProperty)
            return updated;

        return CreateStaticPropertyAccess(replacementProperty);
    }

    private ClassDeclarationSyntax RewriteBaseList(ClassDeclarationSyntax node)
    {
        if (node.BaseList is null)
            return node;

        var rewrittenTypes = new List<BaseTypeSyntax>();

        foreach (var baseType in node.BaseList.Types)
        {
            var baseSymbol =
                _model.GetTypeInfo(baseType.Type, CancellationToken.None).Type as INamedTypeSymbol;
            if (baseSymbol is null)
            {
                rewrittenTypes.Add(baseType);
                continue;
            }

            var map = _fakeBases.FirstOrDefault(m =>
                SymbolEqualityComparer.Default.Equals(m.RealBase, baseSymbol.OriginalDefinition)
            );
            if (map is null)
            {
                rewrittenTypes.Add(baseType);
                continue;
            }

            var fakeName = map.FakeBase.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            rewrittenTypes.Add(SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(fakeName)));
        }

        return node.WithBaseList(
            SyntaxFactory.BaseList(SyntaxFactory.SeparatedList(rewrittenTypes))
        );
    }

    private static bool HasSetProperty(ClassDeclarationSyntax node) =>
        node
            .Members.OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Text == "SetProperty" && m.TypeParameterList is not null);

    private static MethodDeclarationSyntax CreateSetPropertyMember()
    {
        var text = """
            protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? name = null)
            {
                if (EqualityComparer<T>.Default.Equals(storage, value))
                    return false;

                storage = value;
                Raise(name);
                return true;
            }
            """;

        return (MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(text)!;
    }

    private static ExpressionSyntax CreateStaticMethodAccess(IMethodSymbol method)
    {
        var typeName = method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var typeExpression = SyntaxFactory.ParseName(typeName);
        return SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            typeExpression,
            SyntaxFactory.IdentifierName(method.Name)
        );
    }

    private static ExpressionSyntax CreateStaticPropertyAccess(IPropertySymbol prop)
    {
        var typeName = prop.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var typeExpression = SyntaxFactory.ParseName(typeName);
        return SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            typeExpression,
            SyntaxFactory.IdentifierName(prop.Name)
        );
    }
}

internal static class RuleExtraction
{
    public static CallOverwriteRule? TryExtractOverwrite(
        GeneratorSyntaxContext ctx,
        CancellationToken ct
    )
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;

        var invoked = ctx.SemanticModel.GetSymbolInfo(invocation, ct).Symbol as IMethodSymbol;
        if (invoked is null || invoked.Name != "Replace")
            return null;

        if (invoked.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            != "global::Legacy.Tests.Generation.Overwrites")
            return null;

        if (invocation.ArgumentList.Arguments.Count != 2)
            return null;

        if (invocation.ArgumentList.Arguments[0].Expression is not LambdaExpressionSyntax targetLambda)
            return null;

        if (invocation.ArgumentList.Arguments[1].Expression is not LambdaExpressionSyntax replacementLambda)
            return null;

        var targetSymbol = GetReferencedSymbol(ctx.SemanticModel, targetLambda.Body, ct);
        var replacementSymbol = GetReferencedSymbol(ctx.SemanticModel, replacementLambda.Body, ct);

        if (targetSymbol is null || replacementSymbol is null)
            return null;

        return new CallOverwriteRule(
            targetSymbol.OriginalDefinition,
            replacementSymbol.OriginalDefinition
        );
    }

    public static FakeBaseRule? TryExtractFakeBase(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;

        var invoked = ctx.SemanticModel.GetSymbolInfo(invocation, ct).Symbol as IMethodSymbol;
        if (invoked is null || invoked.Name != "Map")
            return null;

        if (invoked.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            != "global::Legacy.Tests.Generation.FakeBases")
            return null;

        if (invoked.TypeArguments.Length != 2)
            return null;

        if (invoked.TypeArguments[0] is not INamedTypeSymbol realBase)
            return null;

        if (invoked.TypeArguments[1] is not INamedTypeSymbol fakeBase)
            return null;

        return new FakeBaseRule(
            realBase.OriginalDefinition,
            fakeBase.OriginalDefinition,
            PublicizeOverrides: true
        );
    }

    private static ISymbol? GetReferencedSymbol(
        SemanticModel model,
        SyntaxNode body, // Changed from ExpressionSyntax to SyntaxNode
        CancellationToken ct
    )
    {
        if (body is not ExpressionSyntax expr)
            return null;

        expr = StripConvert(expr);

        return expr switch
        {
            InvocationExpressionSyntax i => model.GetSymbolInfo(i, ct).Symbol,
            MemberAccessExpressionSyntax m => model.GetSymbolInfo(m, ct).Symbol,
            IdentifierNameSyntax id => model.GetSymbolInfo(id, ct).Symbol,
            ObjectCreationExpressionSyntax o => model.GetSymbolInfo(o, ct).Symbol,
            _ => null,
        };
    }

    private static ExpressionSyntax StripConvert(ExpressionSyntax expr)
    {
        while (expr is ParenthesizedExpressionSyntax p)
            expr = p.Expression;

        while (expr is CastExpressionSyntax c)
            expr = c.Expression;

        while (expr is PrefixUnaryExpressionSyntax u && u.IsKind(SyntaxKind.UnaryPlusExpression))
            expr = u.Operand;

        return expr;
    }
}

internal sealed record CallOverwriteRule(ISymbol Target, ISymbol Replacement);

internal sealed record FakeBaseRule(
    INamedTypeSymbol RealBase,
    INamedTypeSymbol FakeBase,
    bool PublicizeOverrides
);