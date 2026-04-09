### Legacy.App/BaseViewModel.cs

```csharp
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Legacy.App;

public abstract class BaseViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return false;

        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    public virtual Task OnLoadedAsync(CancellationToken ct) => Task.CompletedTask;
}
```

### Legacy.App/HeavyBaseViewModel.cs

```csharp
using System;
using System.IO;

namespace Legacy.App;

public abstract class HeavyBaseViewModel : BaseViewModel
{
    private string _title = "";

    protected HeavyBaseViewModel()
    {
        var seed = File.ReadAllText("app.seed");
        _title = $"Seed:{seed} at {DateTime.Now:O}";
    }

    public virtual void Navigate(string target) { }

    public virtual string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }
}
```

### Legacy.App/IUserModel.cs

```csharp
using System.ComponentModel;

namespace Legacy.App;

public interface IUserModel : INotifyPropertyChanged
{
    string Name { get; }
}
```

### Legacy.App/StringExtensions.cs

```csharp
namespace Legacy.App;

public static class StringExtensions
{
    public static bool IsValidEmail(this string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains("@");
}
```

### Legacy.App/CustomerViewModel.cs

```csharp
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Legacy.App;

public sealed class CustomerViewModel : HeavyBaseViewModel
{
    private readonly IUserModel _user;
    private string _displayName = "";
    private string _email = "";
    private string _status = "";

    public CustomerViewModel(IUserModel user)
    {
        _user = user;
        _user.PropertyChanged += UserOnPropertyChanged;
        UpdateDisplayName();
    }

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public string Email
    {
        get => _email;
        set
        {
            if (SetProperty(ref _email, value))
                Status = value.IsValidEmail() ? "OK" : "Invalid";
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public override Task OnLoadedAsync(CancellationToken ct)
    {
        var mode = File.ReadAllText("app.mode").Trim();
        Title = $"Customer ({mode})";
        if (mode == "go")
            Navigate("Orders");
        return Task.CompletedTask;
    }

    private void UserOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IUserModel.Name) || string.IsNullOrEmpty(e.PropertyName))
            UpdateDisplayName();
    }

    private void UpdateDisplayName() => DisplayName = $"Customer: {_user.Name}";
}
```

---

### Legacy.Tests/Fakes/NotifyBase.cs

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Legacy.Tests.Fakes;

public abstract class NotifyBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

### Legacy.Tests/Fakes/HeavyBaseViewModel_Fake.cs

```csharp
using System.Threading;
using System.Threading.Tasks;

namespace Legacy.Tests.Fakes;

public abstract class HeavyBaseViewModel_Fake : NotifyBase
{
    private string _title = "";

    public string? LastNavigationTarget { get; private set; }

    public virtual Task OnLoadedAsync(CancellationToken ct) => Task.CompletedTask;

    public virtual void Navigate(string target) => LastNavigationTarget = target;

    public virtual string Title
    {
        get => _title;
        set
        {
            if (_title == value)
                return;
            _title = value;
            Raise();
        }
    }
}
```

### Legacy.Tests/TestDoubles/TestClock.cs

```csharp
using System;

namespace Legacy.Tests.TestDoubles;

public static class TestClock
{
    public static DateTime Now { get; set; } = new DateTime(2000, 1, 1);
}
```

### Legacy.Tests/TestDoubles/TestFile.cs

```csharp
using System.Collections.Generic;

namespace Legacy.Tests.TestDoubles;

public static class TestFile
{
    private static readonly Dictionary<string, string> Data = new();

    public static void Clear() => Data.Clear();

    public static void SetText(string path, string text) => Data[path] = text;

    public static string ReadAllText(string path) => Data.TryGetValue(path, out var v) ? v : "";
}
```

### Legacy.Tests/TestDoubles/TestEmail.cs

```csharp
namespace Legacy.Tests.TestDoubles;

public static class TestEmail
{
    public static bool IsValidEmail(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains("@") && value.Contains(".");
}
```

### Legacy.Tests/Generation/Overwrites.cs

```csharp
using System;
using System.Linq.Expressions;

namespace Legacy.Tests.Generation;

public static class Overwrites
{
    public static void Replace<TDelegate>(Expression<TDelegate> target, Expression<TDelegate> replacement)
        where TDelegate : Delegate
    { }
}
```

### Legacy.Tests/Generation/FakeBases.cs

```csharp
namespace Legacy.Tests.Generation;

public static class FakeBases
{
    public static FakeBaseMapBuilder Map<TRealBase, TFakeBase>() => new();

    public sealed class FakeBaseMapBuilder
    {
        public FakeBaseMapBuilder PublicizeOverrides(bool enabled = true) => this;
    }
}
```

### Legacy.Tests/Generation/LegacyRewriteRules.cs

```csharp
using System;
using System.IO;
using Legacy.App;
using Legacy.Tests.Fakes;
using Legacy.Tests.TestDoubles;

namespace Legacy.Tests.Generation;

public static class LegacyRewriteRules
{
    public static void Configure()
    {
        Overwrites.Replace<Func<DateTime>>(
            () => DateTime.Now,
            () => TestClock.Now);

        Overwrites.Replace<Func<string, string>>(
            path => File.ReadAllText(path),
            path => TestFile.ReadAllText(path));

        Overwrites.Replace<Func<string, bool>>(
            s => s.IsValidEmail(),
            s => TestEmail.IsValidEmail(s));

        FakeBases.Map<HeavyBaseViewModel, HeavyBaseViewModel_Fake>().PublicizeOverrides();
    }
}
```

### Legacy.Tests/SutCopy/CustomerViewModel_TestClass.cs

```csharp
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Legacy.App;
using Legacy.Tests.Fakes;
using Legacy.Tests.TestDoubles;

namespace Legacy.Tests.SutCopy;

public sealed class CustomerViewModel_TestClass : HeavyBaseViewModel_Fake
{
    private readonly IUserModel _user;
    private string _displayName = "";
    private string _email = "";
    private string _status = "";

    public CustomerViewModel_TestClass(IUserModel user)
    {
        _user = user;
        _user.PropertyChanged += UserOnPropertyChanged;
        UpdateDisplayName();
    }

    public string DisplayName
    {
        get => _displayName;
        private set
        {
            if (_displayName == value)
                return;
            _displayName = value;
            Raise();
        }
    }

    public string Email
    {
        get => _email;
        set
        {
            if (_email == value)
                return;
            _email = value;
            Raise();
            Status = TestEmail.IsValidEmail(value) ? "OK" : "Invalid";
        }
    }

    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value)
                return;
            _status = value;
            Raise();
        }
    }

    public override Task OnLoadedAsync(CancellationToken ct)
    {
        var mode = TestFile.ReadAllText("app.mode").Trim();
        Title = $"Customer ({mode})";
        if (mode == "go")
            Navigate("Orders");
        return Task.CompletedTask;
    }

    private void UserOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IUserModel.Name) || string.IsNullOrEmpty(e.PropertyName))
            UpdateDisplayName();
    }

    private void UpdateDisplayName() => DisplayName = $"Customer: {_user.Name}";
}
```

### Legacy.Tests/CustomerViewModelTests.cs

```csharp
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Legacy.App;
using Legacy.Tests.SutCopy;
using Legacy.Tests.TestDoubles;
using NSubstitute;
using NUnit.Framework;

namespace Legacy.Tests;

[TestFixture]
public sealed class CustomerViewModelTests
{
    [SetUp]
    public void SetUp()
    {
        TestClock.Now = new System.DateTime(2020, 1, 2, 3, 4, 5, System.DateTimeKind.Utc);
        TestFile.Clear();
    }

    [Test]
    public void Updates_DisplayName_When_User_Name_Changes()
    {
        var user = Substitute.For<IUserModel>();
        user.Name.Returns("Alice");

        var vm = new CustomerViewModel_TestClass(user);
        Assert.That(vm.DisplayName, Is.EqualTo("Customer: Alice"));

        user.Name.Returns("Bob");
        user.PropertyChanged += Raise.Event<PropertyChangedEventHandler>(user, new PropertyChangedEventArgs(nameof(IUserModel.Name)));

        Assert.That(vm.DisplayName, Is.EqualTo("Customer: Bob"));
    }

    [Test]
    public async Task OnLoadedAsync_Sets_Title_And_Navigates()
    {
        var user = Substitute.For<IUserModel>();
        user.Name.Returns("Alice");

        TestFile.SetText("app.mode", "go");

        var vm = new CustomerViewModel_TestClass(user);

        await vm.OnLoadedAsync(CancellationToken.None);

        Assert.That(vm.Title, Is.EqualTo("Customer (go)"));
        Assert.That(vm.LastNavigationTarget, Is.EqualTo("Orders"));
    }

    [Test]
    public void Email_Validation_Uses_TestEmail()
    {
        var user = Substitute.For<IUserModel>();
        user.Name.Returns("Alice");

        var vm = new CustomerViewModel_TestClass(user);

        vm.Email = "not-an-email";
        Assert.That(vm.Status, Is.EqualTo("Invalid"));

        vm.Email = "a@b.com";
        Assert.That(vm.Status, Is.EqualTo("OK"));
    }
}
```

```csharp
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
        var overwriteRules = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax,
                static (ctx, ct) => RuleExtraction.TryExtractOverwrite(ctx, ct))
            .Where(static r => r is not null)
            .Select(static (r, _) => r!)
            .Collect();

        var fakeBaseRules = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax,
                static (ctx, ct) => RuleExtraction.TryExtractFakeBase(ctx, ct))
            .Where(static r => r is not null)
            .Select(static (r, _) => r!)
            .Collect();

        var sutInputs = context.AdditionalTextsProvider
            .Where(static at => at.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(static (at, ct) => new SutInput(at.Path, at.GetText(ct)));

        context.RegisterSourceOutput(
            context.CompilationProvider.Combine(sutInputs.Collect()).Combine(overwriteRules).Combine(fakeBaseRules),
            static (spc, data) =>
            {
                var compilation = data.Left.Left;
                var sutFiles = data.Left.Right;
                var overwrites = data.Right.Left;
                var fakeBases = data.Right.Right;

                foreach (var sut in sutFiles)
                {
                    if (sut.Text is null)
                        continue;

                    var fileName = Path.GetFileName(sut.Path);
                    if (!string.Equals(fileName, "CustomerViewModel.cs", StringComparison.OrdinalIgnoreCase))
                        continue;

                    GenerateCustomerViewModelTestClass(spc, compilation, sut.Text, overwrites, fakeBases);
                }
            });
    }

    private static void GenerateCustomerViewModelTestClass(
        SourceProductionContext spc,
        Compilation compilation,
        SourceText sutSource,
        ImmutableArray<CallOverwriteRule> overwrites,
        ImmutableArray<FakeBaseRule> fakeBases)
    {
        var parseOptions = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions
                           ?? new CSharpParseOptions(LanguageVersion.Preview);

        var sutTree = CSharpSyntaxTree.ParseText(sutSource, parseOptions, cancellationToken: spc.CancellationToken);
        var compilationWithSut = compilation.AddSyntaxTrees(sutTree);
        var model = compilationWithSut.GetSemanticModel(sutTree, ignoreAccessibility: true);

        var root = sutTree.GetCompilationUnitRoot(spc.CancellationToken);
        var originalClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == "CustomerViewModel");
        if (originalClass is null)
            return;

        var rewriter = new TestableSutRewriter(model, overwrites, fakeBases, "CustomerViewModel", "CustomerViewModel_TestClass");
        var rewrittenRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        var generatedClass = rewrittenRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == "CustomerViewModel_TestClass");
        if (generatedClass is null)
            return;

        var originalUsings = root.Usings;

        var extraUsings = new[]
        {
            SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Collections.Generic")),
            SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Runtime.CompilerServices")),
            SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("Legacy.App"))
        };

        var allUsings = originalUsings
            .Concat(extraUsings)
            .GroupBy(u => u.Name.ToString())
            .Select(g => g.First())
            .ToImmutableArray();

        var ns = SyntaxFactory.FileScopedNamespaceDeclaration(SyntaxFactory.ParseName("Legacy.Tests.SutCopy"))
            .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(generatedClass));

        var output = SyntaxFactory.CompilationUnit()
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
        string newClassName)
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

        var rule = _overwrites.FirstOrDefault(r => SymbolEqualityComparer.Default.Equals(r.Target, symbol.OriginalDefinition));
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
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(newArgs)));
        }

        if (expr is MemberAccessExpressionSyntax memberAccess)
        {
            var receiver = (ExpressionSyntax)Visit(memberAccess.Expression)!;

            ExpressionSyntax newCallee = replacementMethod.IsStatic
                ? CreateStaticMethodAccess(replacementMethod)
                : SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    receiver,
                    SyntaxFactory.IdentifierName(replacementMethod.Name));

            return SyntaxFactory.InvocationExpression(newCallee, updated.ArgumentList);
        }

        if (expr is IdentifierNameSyntax && replacementMethod.IsStatic)
        {
            return SyntaxFactory.InvocationExpression(CreateStaticMethodAccess(replacementMethod), updated.ArgumentList);
        }

        return updated;
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var updated = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;

        var symbol = _model.GetSymbolInfo(node, CancellationToken.None).Symbol as IPropertySymbol;
        if (symbol is null)
            return updated;

        var rule = _overwrites.FirstOrDefault(r => SymbolEqualityComparer.Default.Equals(r.Target, symbol.OriginalDefinition));
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
            var baseSymbol = _model.GetTypeInfo(baseType.Type, CancellationToken.None).Type as INamedTypeSymbol;
            if (baseSymbol is null)
            {
                rewrittenTypes.Add(baseType);
                continue;
            }

            var map = _fakeBases.FirstOrDefault(m => SymbolEqualityComparer.Default.Equals(m.RealBase, baseSymbol.OriginalDefinition));
            if (map is null)
            {
                rewrittenTypes.Add(baseType);
                continue;
            }

            var fakeName = map.FakeBase.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            rewrittenTypes.Add(SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(fakeName)));
        }

        return node.WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SeparatedList(rewrittenTypes)));
    }

    private static bool HasSetProperty(ClassDeclarationSyntax node) =>
        node.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == "SetProperty" && m.TypeParameterList is not null);

    private static MethodDeclarationSyntax CreateSetPropertyMember()
    {
        var text =
            """
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
        return SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.ParseTypeName(typeName),
            SyntaxFactory.IdentifierName(method.Name));
    }

    private static ExpressionSyntax CreateStaticPropertyAccess(IPropertySymbol prop)
    {
        var typeName = prop.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.ParseTypeName(typeName),
            SyntaxFactory.IdentifierName(prop.Name));
    }
}

internal static class RuleExtraction
{
    public static CallOverwriteRule? TryExtractOverwrite(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;

        var invoked = ctx.SemanticModel.GetSymbolInfo(invocation, ct).Symbol as IMethodSymbol;
        if (invoked is null)
            return null;

        if (invoked.Name != "Replace")
            return null;

        if (invoked.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) != "global::Legacy.Tests.Generation.Overwrites")
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

        return new CallOverwriteRule(targetSymbol.OriginalDefinition, replacementSymbol.OriginalDefinition);
    }

    public static FakeBaseRule? TryExtractFakeBase(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;

        var invoked = ctx.SemanticModel.GetSymbolInfo(invocation, ct).Symbol as IMethodSymbol;
        if (invoked is null)
            return null;

        if (invoked.Name != "Map")
            return null;

        if (invoked.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) != "global::Legacy.Tests.Generation.FakeBases")
            return null;

        if (invoked.TypeArguments.Length != 2)
            return null;

        if (invoked.TypeArguments[0] is not INamedTypeSymbol realBase)
            return null;

        if (invoked.TypeArguments[1] is not INamedTypeSymbol fakeBase)
            return null;

        return new FakeBaseRule(realBase.OriginalDefinition, fakeBase.OriginalDefinition, PublicizeOverrides: true);
    }

    private static ISymbol? GetReferencedSymbol(SemanticModel model, ExpressionSyntax expr, CancellationToken ct)
    {
        expr = StripConvert(expr);

        return expr switch
        {
            InvocationExpressionSyntax i => model.GetSymbolInfo(i, ct).Symbol,
            MemberAccessExpressionSyntax m => model.GetSymbolInfo(m, ct).Symbol,
            IdentifierNameSyntax id => model.GetSymbolInfo(id, ct).Symbol,
            ObjectCreationExpressionSyntax o => model.GetSymbolInfo(o, ct).Symbol,
            _ => null
        };
    }

    private static ExpressionSyntax StripConvert(ExpressionSyntax expr)
    {
        while (expr is ParenthesizedExpressionSyntax p)
            expr = p.Expression;

        while (expr is CastExpressionSyntax c)
            expr = c.Expression;

        while (expr is UnaryExpressionSyntax u && (u.IsKind(SyntaxKind.ConvertExpression) || u.IsKind(SyntaxKind.UnaryPlusExpression)))
            expr = u.Operand;

        return expr;
    }
}

internal sealed record CallOverwriteRule(ISymbol Target, ISymbol Replacement);
internal sealed record FakeBaseRule(INamedTypeSymbol RealBase, INamedTypeSymbol FakeBase, bool PublicizeOverrides);
```

**How this maps to your approach**

- Put the copied legacy `CustomerViewModel.cs` into the test project as an `AdditionalFile` (so it doesn’t compile directly and doesn’t conflict with the real production type).
- The generator scans your test project code for `Overwrites.Replace(...)` and `FakeBases.Map<,>()` calls and builds symbol-based rewrite maps.
- It parses the copied `CustomerViewModel.cs`, then:
  - rewrites the base type from `HeavyBaseViewModel` to `HeavyBaseViewModel_Fake`
  - rewrites call sites like `File.ReadAllText(...)` and `s.IsValidEmail()` to their test replacements based on the extracted rules
  - renames `CustomerViewModel` + its constructors to `CustomerViewModel_TestClass`
  - injects a minimal `SetProperty<T>` shim into the generated class so it can still compile against the fake base (common need when you bypass a real MVVM base)

If you want, I can also show the project file snippet for `AdditionalFiles`, plus an expanded rewriter that supports property set overwrites and constructor redirects (`new T(...) -> factory(...)`).
