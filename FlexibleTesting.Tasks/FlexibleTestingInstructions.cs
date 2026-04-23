using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;

namespace FlexibleTesting.Tasks;

public class FlexibleTestingPart
{
    public Document Document { get; set; } = null!;
    public ClassDeclarationSyntax Syntax { get; set; } = null!;
}

public class FlexibleTestingInstructions
{
    public INamedTypeSymbol TargetType { get; set; } = null!;
    public List<FlexibleTestingPart> Parts { get; } = new();
    public string OldClassName { get; set; } = string.Empty;
    public string NewClassName { get; set; } = string.Empty;

    public HashSet<IMethodSymbol> MethodsToMakePublic { get; } = new(SymbolSignatureComparer.Default);

    public HashSet<IMethodSymbol> MockMethods { get; } = new(SymbolSignatureComparer.Default);
    public HashSet<IPropertySymbol> MockProperties { get; } = new(SymbolSignatureComparer.Default);
    public HashSet<IFieldSymbol> MockFields { get; } = new(SymbolSignatureComparer.Default);
    public HashSet<INamedTypeSymbol> MockClasses { get; } = new(SymbolEqualityComparer.Default);
    public HashSet<IMethodSymbol> MockClassConstructors { get; } = new(SymbolSignatureComparer.Default);

    /// <summary>
    /// Maps a symbol to its unique name in the dependencies interface.
    /// </summary>
    public Dictionary<ISymbol, string> DependencyMemberNames { get; } = new(SymbolSignatureComparer.Default);

    public string DependenciesInterfaceName { get; set; } = string.Empty;
    public string DependenciesFieldName { get; set; } = string.Empty;
    public string DependenciesParameterName { get; set; } = string.Empty;
    public bool MockInheritance { get; set; }
    public bool RecursiveMockInheritance { get; set; }
    public bool RemoveSealed { get; set; }
    public bool IsPartial { get; set; }
    public List<INamedTypeSymbol> RecursiveBaseTypes { get; } = new();

    public IEnumerable<ISymbol> AllMocks
    {
        get
        {
            foreach (var method in MockMethods)
                yield return method;
            foreach (var property in MockProperties)
                yield return property;
            foreach (var @field in MockFields)
                yield return @field;
        }
    }
}
