namespace FlexibleTesting.Tasks;

internal sealed class FlexibleTestingNamePolicy : IFlexibleTestingNamePolicy
{
    public string DependenciesFieldName => FlexibleTestingGeneratedNames.DependenciesFieldName;

    public string BaseDependenciesFieldName => FlexibleTestingGeneratedNames.BaseDependenciesFieldName;

    public string DependenciesParameterName => FlexibleTestingGeneratedNames.DependenciesParameterName;

    public string BaseDependenciesParameterName => FlexibleTestingGeneratedNames.BaseDependenciesParameterName;

    public string GetGeneratedClassName(string oldClassName)
    {
        return FlexibleTestingGeneratedNames.GetGeneratedClassName(oldClassName);
    }

    public string GetBaseGeneratedClassName(string oldClassName)
    {
        return FlexibleTestingGeneratedNames.GetBaseGeneratedClassName(oldClassName);
    }

    public string GetDependenciesInterfaceName(string oldClassName)
    {
        return FlexibleTestingGeneratedNames.GetDependenciesInterfaceName(oldClassName);
    }

    public string GetBaseDependenciesInterfaceName(string oldClassName)
    {
        return FlexibleTestingGeneratedNames.GetBaseDependenciesInterfaceName(oldClassName);
    }

    public string GetMockClassInterfaceName(string mockedTypeName)
    {
        return FlexibleTestingGeneratedNames.GetMockClassInterfaceName(mockedTypeName);
    }
}
