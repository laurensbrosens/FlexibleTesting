using FlexibleTestingDomain;
using LegacyCodeProjectCore;

namespace LegacyCodeProjectTests;

[GeneratorInstructions]
internal class ViewModelCoreBuilder(string testString) : ViewModelCore(testString), IGeneratorInstructions
{
    public void Configure()
    {
        Overwrites.ForClass<ViewModelCore>();
        Overwrites.Mock(() => DateTime.Now);
        Overwrites.RecursiveMockInheritance();
    }
}
