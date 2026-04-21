using Microsoft.CodeAnalysis;
using System.Collections.Generic;

namespace FlexibleTesting.Tasks;

public class FlexibleTestingInstructions
{
    public INamedTypeSymbol TargetType { get; set; }
    public string OldClassName { get; set; }
    public string NewClassName { get; set; }
    
    public HashSet<IMethodSymbol> MethodsToMakePublic { get; } = new(SymbolSignatureComparer.Default);
    
    public HashSet<IMethodSymbol> MockableMethods { get; } = new(SymbolSignatureComparer.Default);
    public HashSet<IPropertySymbol> MockableProperties { get; } = new(SymbolSignatureComparer.Default);
    public HashSet<IFieldSymbol> MockableFields { get; } = new(SymbolSignatureComparer.Default);

    /// <summary>
    /// Maps a symbol to its unique name in the dependencies interface.
    /// </summary>
    public Dictionary<ISymbol, string> DependencyMemberNames { get; } = new(SymbolSignatureComparer.Default);

    public string DependenciesInterfaceName { get; set; }
    public string DependenciesFieldName { get; set; }
    public string DependenciesParameterName { get; set; }
    public bool MockInheritance { get; set; }

    public IEnumerable<ISymbol> AllMockables
    {
        get
        {
            foreach (var method in MockableMethods) yield return method;
            foreach (var property in MockableProperties) yield return property;
            foreach (var @field in MockableFields) yield return @field;
        }
    }
}
