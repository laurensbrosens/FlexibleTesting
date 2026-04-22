using FlexibleTestingDomain;
using LegacyCodeProject.Core;
using LegacyCodeProject.Viewmodels;

namespace LegacyCodeProjectTests;

[GeneratorInstructions]
internal class UserViewModelBaseBuilder(SomeDataObject someDataObject) : UserViewModelBase(someDataObject), IGeneratorInstructions
{
    public void Configure()
    {
        Overwrites.ForClass<UserViewModelBase>();
        Overwrites.Mock(() => DateTime.Now);
        Overwrites.Mock<UserService>();
        Overwrites.RecursiveMockInheritance();
    }
}
