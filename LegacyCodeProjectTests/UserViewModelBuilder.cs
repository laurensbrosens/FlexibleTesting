using FlexibleTestingDomain;
using LegacyCodeProject.Core;
using LegacyCodeProject.Viewmodels;

namespace LegacyCodeProjectTests;

[GeneratorInstructions]
internal class UserViewModelBuilder(SomeDataObject someDataObject) : UserViewModel(someDataObject), IGeneratorInstructions
{
    public void Configure()
    {
        Overwrites.ForClass<UserViewModel>();
        Overwrites.Mock(() => Guid.NewGuid());
        Overwrites.RecursiveMockInheritance();
    }
}
