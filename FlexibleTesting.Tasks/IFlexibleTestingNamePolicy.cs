namespace FlexibleTesting.Tasks;

internal interface IFlexibleTestingNamePolicy
{
    string DependenciesFieldName { get; }

    string BaseDependenciesFieldName { get; }

    string DependenciesParameterName { get; }

    string BaseDependenciesParameterName { get; }

    string GetGeneratedClassName(string oldClassName);

    string GetBaseGeneratedClassName(string oldClassName);

    string GetDependenciesInterfaceName(string oldClassName);

    string GetBaseDependenciesInterfaceName(string oldClassName);

    string GetMockClassInterfaceName(string mockedTypeName);
}
