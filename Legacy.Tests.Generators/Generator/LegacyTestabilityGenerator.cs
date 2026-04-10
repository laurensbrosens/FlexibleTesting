#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Legacy.Tests.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class LegacyTestabilityGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor TargetNotInSource = new(
        id: "LTG001",
        title: "Target type must be present as source in this compilation",
        messageFormat: "Cannot generate testable copy for '{0}' because it is not present as source (no declaring syntax). Copy/link the file into the test project (or otherwise compile it as source).",
        category: "LegacyTestability",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MultiplePartials = new(
        id: "LTG002",
        title: "Partial type has multiple syntax parts",
        messageFormat: "Type '{0}' has multiple syntax parts. PoC generator only uses the first part; generation may be incomplete.",
        category: "LegacyTestability",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InjectMemberParseFailed = new(
        id: "LTG003",
        title: "Injected member could not be parsed",
        messageFormat: "Failed to parse injected member for '{0}': {1}",
        category: "LegacyTestability",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor RewriteMethodNotFound = new(
        id: "LTG004",
        title: "Rewrite requested for method '{0}' on '{1}', but no matching method declarations were found.",
        messageFormat: "Rewrite requested for method '{0}' on '{1}', but no matching method declarations were found.",
        category: "LegacyTestability",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Find classes that *might* derive from LegacyTestability.TestabilityInstructions
        var instructionClasses = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (n, _) => n is ClassDeclarationSyntax cds && cds.BaseList is not null,
                transform: static (ctx, ct) => TryGetInstructionClass(ctx, ct))
            .Where(static x => x.HasValue)
            .Select(static (x, _) => x!.Value); // Now it's a nullable tuple, so get its value

        // From each instruction class, extract target rules
        var rules = instructionClasses.Select(static (data, ct) => ExtractRules(data.Symbol, data.Model, data.Compilation, ct));

        // Flatten all extracted rules and generate
        var allRules = rules.SelectMany(static (rulesWithComp, _) => rulesWithComp);

        context.RegisterSourceOutput(allRules, static (spc, ruleAndComp) => Generate(spc, ruleAndComp.Rule, ruleAndComp.Compilation));
    }

    private static (INamedTypeSymbol Symbol, SemanticModel Model, Compilation Compilation)? TryGetInstructionClass(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var cds = (ClassDeclarationSyntax)ctx.Node;
        var symbol = ctx.SemanticModel.GetDeclaredSymbol(cds, ct) as INamedTypeSymbol;
        if (symbol is null)
            return null;

        // Check base types chain for LegacyTestability.TestabilityInstructions
        for (INamedTypeSymbol? cur = symbol; cur is not null; cur = cur.BaseType)
        {
            if (cur.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::LegacyTestability.TestabilityInstructions")
                return (symbol, ctx.SemanticModel, ctx.SemanticModel.Compilation);
        }

        return null;
    }

    private static ImmutableArray<TargetRule> ExtractRules(INamedTypeSymbol instructionClass, SemanticModel semanticModel, Compilation compilation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Find Configure() method
        var configure = instructionClass
            .GetMembers()
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.Name == "Configure" && m.Parameters.Length == 0);

        if (configure is null)
            return ImmutableArray<TargetRule>.Empty;

        var rules = new List<TargetRule>();

        foreach (var syntaxRef in configure.DeclaringSyntaxReferences)
        {
            ct.ThrowIfCancellationRequested();

            if (syntaxRef.GetSyntax(ct) is not MethodDeclarationSyntax mds)
                continue;

            if (mds.Body is null)
                continue;

            foreach (var stmt in mds.Body.Statements.OfType<ExpressionStatementSyntax>())
            {
                ct.ThrowIfCancellationRequested();

                if (stmt.Expression is not InvocationExpressionSyntax outerInvocation)
                    continue;

                if (!TryParseChain(semanticModel, outerInvocation, ct, out var parsedRule))
                    continue;

                rules.Add(parsedRule);
            }
        }

        // Merge by (TargetType, Suffix) so multiple chains can contribute.
        var merged = rules
            .GroupBy(r => (Target: r.TargetType, r.Suffix), SymbolTupleComparer.Instance)
            .Select(g => TargetRule.Merge(g.First().TargetType, g.Key.Suffix, g))
            .ToImmutableArray();

        return merged;
    }

    private static bool TryParseChain(
        SemanticModel semanticModel,
        InvocationExpressionSyntax outerInvocation,
        CancellationToken ct,
        out TargetRule rule)
    {
        ct.ThrowIfCancellationRequested();

        rule = default;

        // Flatten chain: For<T>().WithSuffix(...).Publicize(...).RewriteMethod(...).InjectMember(...)
        // We'll collect calls from inside-out, then reverse.
        var calls = new List<(string Name, ArgumentListSyntax Args, Location Location)>();

        InvocationExpressionSyntax? curInvoke = outerInvocation;

        while (curInvoke is not null)
        {
            ct.ThrowIfCancellationRequested();

            if (curInvoke.Expression is MemberAccessExpressionSyntax maes)
            {
                var name = maes.Name.Identifier.ValueText;
                calls.Add((name, curInvoke.ArgumentList, curInvoke.GetLocation()));
                curInvoke = maes.Expression as InvocationExpressionSyntax;
                continue;
            }

            // Root invocation (expected: For<TTarget>())
            break;
        }

        if (curInvoke is null)
            return false;

        var rootInvoke = curInvoke;

        // Root must be For<TTarget>()
        var rootSymbol = semanticModel.GetSymbolInfo(rootInvoke, ct).Symbol as IMethodSymbol;
        if (rootSymbol is null)
            return false;

        if (rootSymbol.Name != "For")
            return false;

        if (rootSymbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) != "global::LegacyTestability.TestabilityInstructions")
            return false;

        if (rootSymbol.TypeArguments.Length != 1)
            return false;

        if (rootSymbol.TypeArguments[0] is not INamedTypeSymbol targetType)
            return false;

        // Defaults
        string suffix = "_TestClass";
        var publicize = new HashSet<string>(StringComparer.Ordinal);
        var rewrites = new List<(string MethodName, string Body, Location Loc)>();
        var injects = new List<(string MemberSource, Location Loc)>();

        // We collected outer->inner excluding the root; reverse to apply in written order.
        calls.Reverse();

        foreach (var (name, args, loc) in calls)
        {
            ct.ThrowIfCancellationRequested();

            switch (name)
            {
                case "WithSuffix":
                {
                    if (TryGetSingleStringLiteral(args, out var s))
                        suffix = s;
                    break;
                }
                case "Publicize":
                {
                    foreach (var s in GetAllStringLiterals(args))
                        publicize.Add(s);
                    break;
                }
                case "RewriteMethod":
                {
                    if (args.Arguments.Count == 2
                        && TryGetStringLiteral(args.Arguments[0].Expression, out var methodName)
                        && TryGetStringLiteral(args.Arguments[1].Expression, out var body))
                    {
                        rewrites.Add((methodName, body, loc));
                    }
                    break;
                }
                case "InjectMember":
                {
                    if (TryGetSingleStringLiteral(args, out var memberSrc))
                        injects.Add((memberSrc, loc));
                    break;
                }
            }
        }

        rule = new TargetRule(
            TargetType: targetType,
            Suffix: suffix,
            PublicizeMembers: publicize.ToImmutableArray(),
            RewriteMethods: rewrites.ToImmutableArray(),
            InjectMembers: injects.ToImmutableArray());

        return true;
    }

    private static bool TryGetSingleStringLiteral(ArgumentListSyntax args, out string value)
    {
        value = "";
        if (args.Arguments.Count != 1)
            return false;

        return TryGetStringLiteral(args.Arguments[0].Expression, out value);
    }

    private static IEnumerable<string> GetAllStringLiterals(ArgumentListSyntax args)
    {
        foreach (var a in args.Arguments)
        {
            if (TryGetStringLiteral(a.Expression, out var s))
                yield return s;
        }
    }

    private static bool TryGetStringLiteral(ExpressionSyntax expr, out string value)
    {
        value = "";

        if (expr is LiteralExpressionSyntax les &&
            les.IsKind(SyntaxKind.StringLiteralExpression))
        {
            value = les.Token.ValueText;
            return true;
        }

        // raw string literal and interpolated strings are not handled in this PoC
        return false;
    }

        context.RegisterSourceOutput(allRules, static (spc, ruleAndComp) => Generate(spc, ruleAndComp.Rule, ruleAndComp.Compilation));

    private static void Generate(SourceProductionContext spc, TargetRule rule, Compilation compilation)
    {
        var target = rule.TargetType;

        if (target.DeclaringSyntaxReferences.Length == 0)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                TargetNotInSource, Location.None, target.ToDisplayString()));
            return;
        }

        if (target.DeclaringSyntaxReferences.Length > 1)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                MultiplePartials, Location.None, target.ToDisplayString()));
        }

        var typeSyntaxRef = target.DeclaringSyntaxReferences[0];
        if (typeSyntaxRef.GetSyntax() is not ClassDeclarationSyntax classDecl)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                TargetNotInSource, Location.None, target.ToDisplayString()));
            return;
        }

        var semanticModel = compilation.GetSemanticModel(classDecl.SyntaxTree);
        var newName = target.Name + rule.Suffix;

        // Rewrite the class
        var rewriter = new CloneRewriter(semanticModel, target, newName, rule, spc);
        var newClass = (ClassDeclarationSyntax)rewriter.Visit(classDecl)!;

        // Build new compilation unit: preserve root usings; emit the rewritten class into the same namespace.
        var root = classDecl.SyntaxTree.GetCompilationUnitRoot();

        var ns = classDecl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        MemberDeclarationSyntax containerMember;

        if (ns is FileScopedNamespaceDeclarationSyntax fs)
        {
            containerMember = SyntaxFactory.FileScopedNamespaceDeclaration(fs.Name)
                .WithAttributeLists(fs.AttributeLists)
                .WithModifiers(fs.Modifiers)
                .WithLeadingTrivia(fs.GetLeadingTrivia())
                .WithTrailingTrivia(fs.GetTrailingTrivia())
                .AddMembers(newClass);
        }
        else if (ns is NamespaceDeclarationSyntax bns)
        {
            containerMember = SyntaxFactory.NamespaceDeclaration(bns.Name)
                .WithAttributeLists(bns.AttributeLists)
                .WithModifiers(bns.Modifiers)
                .WithLeadingTrivia(bns.GetLeadingTrivia())
                .WithTrailingTrivia(bns.GetTrailingTrivia())
                .AddMembers(newClass);
        }
        else
        {
            // No namespace; emit at top-level
            containerMember = newClass;
        }

        var cu = SyntaxFactory.CompilationUnit()
            .WithLeadingTrivia(root.GetLeadingTrivia())
            .WithUsings(root.Usings)
            .AddMembers(containerMember)
            .WithTrailingTrivia(root.GetTrailingTrivia())
            .NormalizeWhitespace();

        var hintName = $"{target.Name}{rule.Suffix}.g.cs";
        spc.AddSource(hintName, cu.ToFullString());
    }

    private sealed class CloneRewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _semanticModel;
        private readonly INamedTypeSymbol _originalType;
        private readonly string _newTypeName;
        private readonly TargetRule _rule;
        private readonly SourceProductionContext _spc;

        private readonly HashSet<string> _publicize;
        private readonly ImmutableArray<(string MethodName, string Body, Location Loc)> _rewrites;
        private readonly ImmutableArray<(string MemberSource, Location Loc)> _injects;

        private bool _anyRewriteMatched;

        public CloneRewriter(
            SemanticModel semanticModel,
            INamedTypeSymbol originalType,
            string newTypeName,
            TargetRule rule,
            SourceProductionContext spc)
        {
            _semanticModel = semanticModel;
            _originalType = originalType;
            _newTypeName = newTypeName;
            _rule = rule;
            _spc = spc;

            _publicize = new HashSet<string>(_rule.PublicizeMembers, StringComparer.Ordinal);
            _rewrites = _rule.RewriteMethods;
            _injects = _rule.InjectMembers;
        }

        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            // Rename class
            var rewritten = node.WithIdentifier(SyntaxFactory.Identifier(_newTypeName));

            // Rewrite members
            rewritten = (ClassDeclarationSyntax)base.VisitClassDeclaration(rewritten)!;

            // Append injected members
            if (_injects.Length > 0)
            {
                var members = rewritten.Members.ToList();

                foreach (var (src, loc) in _injects)
                {
                    var member = SyntaxFactory.ParseMemberDeclaration(src);
                    if (member is null)
                    {
                        _spc.ReportDiagnostic(Diagnostic.Create(
                            InjectMemberParseFailed, loc, _originalType.ToDisplayString(), src));
                        continue;
                    }

                    members.Add(member);
                }

                rewritten = rewritten.WithMembers(SyntaxFactory.List(members));
            }

            // If rewrite requested but none matched, warn per method.
            foreach (var grp in _rewrites.GroupBy(r => r.MethodName, StringComparer.Ordinal))
            {
                // we mark matches per visit; easiest PoC: detect by scanning the final tree
                var found = rewritten.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .Any(m => m.Identifier.ValueText == grp.Key);

                if (!found)
                {
                    var anyLoc = grp.First().Loc;
                    _spc.ReportDiagnostic(Diagnostic.Create(
                        RewriteMethodNotFound, anyLoc, grp.Key, _originalType.ToDisplayString()));
                }
            }

            return rewritten;
        }

        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            // Ensure ctor name matches new type name
            var updated = node.WithIdentifier(SyntaxFactory.Identifier(_newTypeName));
            updated = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(updated)!;

            // Publicize ctor if requested by name (ctor name is the type name in source; users may not target it)
            // We'll not support publicizing by ctor name in this PoC; keep as-is unless they publicize by member list
            // (won't match due to rename). This is intentionally minimal.

            return updated;
        }

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var updated = node;

            // Publicize if name matches
            if (_publicize.Contains(updated.Identifier.ValueText))
                updated = updated.WithModifiers(Publicize(updated.Modifiers));

            // Rewrite body if requested
            foreach (var r in _rewrites)
            {
                if (!string.Equals(r.MethodName, updated.Identifier.ValueText, StringComparison.Ordinal))
                    continue;

                // Replace method body with parsed block
                var block = SyntaxFactory.ParseStatement("{" + r.Body + "}") as BlockSyntax;
                if (block is null)
                    block = SyntaxFactory.Block();

                updated = updated
                    .WithExpressionBody(null)
                    .WithSemicolonToken(default)
                    .WithBody(block);

                _anyRewriteMatched = true;
                break; // first match wins (you can add multiple rules; PoC keeps first)
            }

            return base.VisitMethodDeclaration(updated);
        }

        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            var updated = node;
            if (_publicize.Contains(updated.Identifier.ValueText))
                updated = updated.WithModifiers(Publicize(updated.Modifiers));

            return base.VisitPropertyDeclaration(updated);
        }

        public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            // Publicize if any variable matches
            var anyMatch = node.Declaration.Variables.Any(v => _publicize.Contains(v.Identifier.ValueText));
            if (!anyMatch)
                return base.VisitFieldDeclaration(node);

            var updated = node.WithModifiers(Publicize(node.Modifiers));
            return base.VisitFieldDeclaration(updated);
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            // If the identifier refers to the original type symbol, rewrite to the new type name.
            // Helps simple self-references like typeof(OriginalType) or OriginalType.StaticMember
            var symbol = _semanticModel.GetSymbolInfo(node).Symbol;
            if (symbol is INamedTypeSymbol nts &&
                SymbolEqualityComparer.Default.Equals(nts, _originalType))
            {
                return SyntaxFactory.IdentifierName(_newTypeName)
                    .WithLeadingTrivia(node.GetLeadingTrivia())
                    .WithTrailingTrivia(node.GetTrailingTrivia());
            }

            return base.VisitIdentifierName(node);
        }

        private static SyntaxTokenList Publicize(SyntaxTokenList modifiers)
        {
            // Remove private/protected/internal, add public if not already.
            var kept = new List<SyntaxToken>(modifiers.Count);

            foreach (var m in modifiers)
            {
                if (m.IsKind(SyntaxKind.PrivateKeyword)) continue;
                if (m.IsKind(SyntaxKind.ProtectedKeyword)) continue;
                if (m.IsKind(SyntaxKind.InternalKeyword)) continue;

                kept.Add(m);
            }

            if (!kept.Any(t => t.IsKind(SyntaxKind.PublicKeyword)))
                kept.Insert(0, SyntaxFactory.Token(SyntaxKind.PublicKeyword));

            return SyntaxFactory.TokenList(kept);
        }
    }

    private readonly record struct TargetRule(
        INamedTypeSymbol TargetType,
        string Suffix,
        ImmutableArray<string> PublicizeMembers,
        ImmutableArray<(string MethodName, string Body, Location Loc)> RewriteMethods,
        ImmutableArray<(string MemberSource, Location Loc)> InjectMembers)
    {
        public static TargetRule Merge(
            INamedTypeSymbol targetType,
            string suffix,
            IEnumerable<TargetRule> rules)
        {
            var pub = rules.SelectMany(r => r.PublicizeMembers).Distinct(StringComparer.Ordinal).ToImmutableArray();
            var rew = rules.SelectMany(r => r.RewriteMethods).ToImmutableArray();
            var inj = rules.SelectMany(r => r.InjectMembers).ToImmutableArray();
            return new TargetRule(targetType, suffix, pub, rew, inj);
        }
    }

    private sealed class SymbolTupleComparer : IEqualityComparer<(INamedTypeSymbol Target, string Suffix)>
    {
        public static readonly SymbolTupleComparer Instance = new();

        public bool Equals((INamedTypeSymbol Target, string Suffix) x, (INamedTypeSymbol Target, string Suffix) y)
            => SymbolEqualityComparer.Default.Equals(x.Target, y.Target)
               && StringComparer.Ordinal.Equals(x.Suffix, y.Suffix);

        public int GetHashCode((INamedTypeSymbol Target, string Suffix) obj)
        {
            var h1 = SymbolEqualityComparer.Default.GetHashCode(obj.Target);
            var h2 = StringComparer.Ordinal.GetHashCode(obj.Suffix);
            unchecked { return (h1 * 397) ^ h2; }
        }
    }
}
