namespace FlexibleTesting.Tasks;

internal static class FlexibleTestingGeneratedNames
{
    public const string DependenciesFieldName = "_dependencies";
    public const string BaseDependenciesFieldName = "_baseDependencies";
    public const string DependenciesParameterName = "dependencies";
    public const string BaseDependenciesParameterName = "baseDependencies";
    public const string GeneratedClassSuffix = "_G";
    public const string BaseGeneratedClassSuffix = "Base_G";
    public const string DependenciesInterfacePrefix = "IAuto";
    public const string SystemNamespaceName = "System";

    public static string GetGeneratedClassName(string oldClassName)
    {
        return $"{oldClassName}{GeneratedClassSuffix}";
    }

    public static string GetBaseGeneratedClassName(string oldClassName)
    {
        return $"{oldClassName}{BaseGeneratedClassSuffix}";
    }

    public static string GetDependenciesInterfaceName(string oldClassName)
    {
        return $"{DependenciesInterfacePrefix}{oldClassName}Dependencies";
    }

    public static string GetBaseDependenciesInterfaceName(string oldClassName)
    {
        return $"{DependenciesInterfacePrefix}{oldClassName}BaseDependencies";
    }

    public static string GetMockClassInterfaceName(string mockedTypeName)
    {
        return $"{DependenciesInterfacePrefix}{mockedTypeName}";
    }
}
