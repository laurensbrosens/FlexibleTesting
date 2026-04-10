#nullable enable
namespace LegacyTestability;

/// <summary>
/// Developer-authored instruction classes should derive from this and implement <see cref="Configure"/>.
/// All fluent methods are no-ops at runtime; the source generator parses the syntax/semantic model.
/// </summary>
public abstract class TestabilityInstructions
{
    /// <summary>
    /// Implement this and write fluent rules inside. The generator reads the call chain(s).
    /// </summary>
    public abstract void Configure();

    /// <summary>
    /// Start a rule chain for a type that exists as source in the current compilation
    /// (e.g., a copied/link-to-source legacy class in the test project).
    /// </summary>
    protected TargetBuilder<TTarget> For<TTarget>() => new();

    /// <summary>
    /// Fluent builder (no-op at runtime). The generator inspects calls to these methods.
    /// </summary>
    public sealed class TargetBuilder<TTarget>
    {
        /// <summary>Defaults to "_TestClass".</summary>
        public TargetBuilder<TTarget> WithSuffix(string suffix) => this;

        /// <summary>
        /// Publicize members by name (methods, properties, fields).
        /// If a name matches multiple overloads, all matching members are publicized.
        /// </summary>
        public TargetBuilder<TTarget> Publicize(params string[] memberNames) => this;

        /// <summary>
        /// Replace the body of all methods with the given name.
        /// <paramref name="newBodyStatements"/> must be the contents of a block (no surrounding braces required).
        /// </summary>
        public TargetBuilder<TTarget> RewriteMethod(string methodName, string newBodyStatements) => this;

        /// <summary>
        /// Inject raw member source code into the generated class (e.g. "public int X() => 1;").
        /// </summary>
        public TargetBuilder<TTarget> InjectMember(string memberSourceCode) => this;
    }
}
