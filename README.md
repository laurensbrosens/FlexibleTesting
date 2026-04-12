Somewhat relevant example:
https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.cookbook.md#unit-testing-of-generators
Use AddEmbeddedAttributeDefinition to prevent problems with duplicate attribute names

If found no examples of creating a builder to specify on how a generator works. I wonder if it is performant to find a 
class file based on class type? The cookbook specifically says that using an interface as marker instead of an attribute
is significantly less efficient and considered bad practice (for source generators).

Debugging is hard, but exceptions seem to work (visible in build output)

Limitation: Generated code has to be in the same project as the legacy code (because I can't copy it's content otherwise).

## Goal

Make extremely large legacy C# code unit-testable (including WPF ViewModels) without heavy refactors, by compiling a “testable copy” and applying controlled rewrites (dependencies, inheritance) via a source generator (+ analyzer). The developer writes normal unit tests (xUnit/NUnit) and can still use NSubstitute/FluentValidation.

---

## Final approach (high level)

1. Copy the SUT source file(s) into a test project (or a dedicated “generated tests” project).
2. Lock the copied file(s) against the original (checksum/AST fingerprint) so changes in production invalidate the test copy and force re-sync.
3. Apply source-generation rewrites to make code testable without hitting IO/network/time/etc., using two mechanisms:
   - Call-site overwrites (symbol-based) using strongly typed `Expression<TDelegate>` rules.
   - Base-type replacement (inheritance rewrite) so heavy base constructors never run (common for legacy MVVM base classes).
4. Optional: “publicize” members so tests can directly call what used to be `protected`/`internal`.
5. Run normal unit tests against the generated/testable types.

---

## One step-by-step workflow (developer experience)

1. Enable internal visibility (only if you need it)
   - In the production assembly: `InternalsVisibleTo("Legacy.Tests")` so copied code can compile when it touches `internal` members.

2. Copy legacy source into a testable project
   - Keep original `using`s and compile with the same NuGet/project references to avoid “missing types” noise.

3. Add a lock check
   - Your tool stores a fingerprint of the production file. If production changes, the test copy is marked stale and the build fails with a clear message (“re-copy required”).

4. Define rewrite rules in code (compile-time checked)
   - One registry for overwriting method/property/constructor call sites.
   - One registry for base-type replacement to avoid running heavy base constructors.

5. (Optional) Enable “PublicizeOverrides”
   - Generator rewrites members in the copied SUT to be `public` so tests can call them directly.

6. Write normal unit tests
   - Use NUnit + NSubstitute/FluentValidation as usual.

---

## Consistent developer-side code examples (fluent style)

### 1) Call-site overwrites (static + instance + extension methods)

```csharp
namespace Legacy.Tests.Generation;

using System.Linq.Expressions;
using System.IO;

public static class Overwrites
{
    public static void Replace<TDelegate>(Expression<TDelegate> target, Expression<TDelegate> replacement)
        where TDelegate : Delegate
    { }
}

public static class LegacyOverwriteRules
{
    public static void Configure()
    {
        Overwrites.Replace<Func<DateTime>>(
            () => DateTime.Now,
            () => TestClock.Now);

        Overwrites.Replace<Func<string, string>>(
            path => File.ReadAllText(path),
            path => TestFile.ReadAllText(path));

        // Extension methods also work: the expression resolves to the extension method symbol.
        Overwrites.Replace<Func<string, bool>>(
            s => s.IsValidEmail(),
            s => TestEmail.IsValidEmail(s));
    }
}
```

### 2) Property get/set overwrites (strongly typed)

```csharp
namespace Legacy.Tests.Generation;

using System.Linq.Expressions;

public static class PropertyOverwrites
{
    public static void ReplaceGet<TDelegate>(Expression<TDelegate> target, Expression<TDelegate> replacement)
        where TDelegate : Delegate
    { }

    public static void ReplaceSet<TDelegate>(Expression<TDelegate> target, Expression<TDelegate> replacement)
        where TDelegate : Delegate
    { }
}

public static class LegacyPropertyRules
{
    public static void Configure()
    {
        PropertyOverwrites.ReplaceGet<Func<User, string>>(
            u => u.HomeDir,
            u => TestUserPaths.HomeDir(u));

        PropertyOverwrites.ReplaceSet<Action<User, string>>(
            (u, v) => u.HomeDir = v,
            (u, v) => TestUserPaths.SetHomeDir(u, v));
    }
}
```

### 3) Constructor overwrites (redirect `new` to a factory)

```csharp
namespace Legacy.Tests.Generation;

using System.Linq.Expressions;

public static class ConstructorOverwrites
{
    public static void Replace<TDelegate>(Expression<TDelegate> target, Expression<TDelegate> replacement)
        where TDelegate : Delegate
    { }
}

public static class LegacyConstructorRules
{
    public static void Configure()
    {
        ConstructorOverwrites.Replace<Func<string, SqlRepository>>(
            cs => new SqlRepository(cs),
            cs => TestFactories.CreateSqlRepository(cs));
    }
}
```

### 4) Base-type replacement (inheritance rewrite) with developer-authored fake base

This solves “huge base class + ctor must never run” (common MVVM issue). The developer writes the fake base themselves; the generator only rewrites inheritance.

```csharp
namespace Legacy.Tests.Fakes;

using System.ComponentModel;
using System.Runtime.CompilerServices;

public abstract class NotifyBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// Developer-authored fake base for the heavy legacy base:
public abstract class HeavyBaseViewModel_Fake : NotifyBase
{
    public virtual Task OnLoadedAsync(CancellationToken ct) => Task.CompletedTask;

    public virtual void Navigate(string target) { /* record/log if needed */ }

    public virtual string Title { get; set; } = "";
}
```

Mapping rule (generator reads this, then rewrites inheritance in the copied SUT):

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

public static class LegacyFakeBaseRules
{
    public static void Configure()
    {
        FakeBases
            .Map<HeavyBaseViewModel, HeavyBaseViewModel_Fake>()
            .PublicizeOverrides(); // optional default
    }
}
```

What the generator does to the copied SUT:

- `class CustomerViewModel : HeavyBaseViewModel` → `class CustomerViewModel : HeavyBaseViewModel_Fake`
- If `PublicizeOverrides` is enabled, it rewrites `protected override` → `public override` so tests can call those members directly.

---

## Diagnostics (recommended)

Add a Roslyn analyzer (or generator diagnostics) to improve the developer experience:

- Detect missing members on the fake base and report a single clear error (“HeavyBaseViewModel_Fake is missing virtual member X required by CustomerViewModel”), instead of dozens of cascading compiler errors.
- Validate overwrite rules: ambiguous overloads, unsupported expression shapes, replacement signature mismatch, etc.
- Report “rule matches 0 call sites” (useful when legacy code changes).

---

## Features and modes (summary)

Core features

- File-copy-based test compilation with lock/fingerprint invalidation.
- References and `using`s allowed (compiles with real type info).
- Expression-based overwrites for:
  - static methods/properties (`DateTime.Now`, `Guid.NewGuid`)
  - instance methods/properties
  - extension methods
  - constructors (`new T(...)`)
- Base-type replacement via inheritance rewrite (avoid heavy base constructors).
- Optional “PublicizeOverrides” mode to rewrite accessibility to `public` for direct testing.
- Analyzer-backed diagnostics for missing fake members and invalid overwrite rules.

Suggested modes

- Safe mode: only allow call-site overwrites (Expression rules). No free-form edits. Best for keeping tests representative.
- Flexible mode: allow base-type replacement + publicize overrides + call-site overwrites (still rule-driven).
- All mode: allow arbitrary edits to the copied source (maximum power, highest drift risk). Useful as an escape hatch.

---

## Advantages

- Makes very large legacy code testable without refactoring the production project.
- Works for hard-to-mock patterns (static calls, constructors, extension methods) because rewrites happen at the source level.
- Enables fast ViewModel unit tests even when the real base constructor is unusable (replace base with fake).
- Strong compile-time feedback for overwrite rules (expression + generics ensure signature compatibility).
- Keeps tests “normal”: xUnit/NUnit, plus NSubstitute/FluentValidation remain usable.

---

## Disadvantages / risks

- You are effectively testing a generated/copy variant; drift is a real risk if not controlled.
- Requires generator/analyzer engineering effort (especially for good diagnostics and robust symbol matching).
- Publicizing members changes encapsulation in the test copy (acceptable for test-only code, but still a semantic change).
- Maintenance overhead: when production code changes, lock invalidation forces re-copy/regen and potentially fake-base updates.
