using FlexibleTestingDomain;
using FlexibleTestingDomain.Templates;
using LegacyCodeProject.Core;
using LegacyCodeProject.Viewmodels;

namespace LegacyCodeProjectTests;

[GeneratorInstructions]
internal class UserViewModelBuilder(SomeDataObject someDataObject) : UserViewModel<string>(someDataObject), IGeneratorInstructions
{
    public void Configure()
    {
        Overwrites.ForClass(typeof(UserViewModel<>));
        Overwrites.Include<CommonDotNetGeneratorInstructions>();
        Overwrites.Mock(() => Now);
        Overwrites.Mock(() => LegacyCodeProject.Core.First.Clock.Now);
        Overwrites.Mock(() => LegacyCodeProject.Core.Second.Clock.Now);
        Overwrites.RecursiveMockInheritance();
        Overwrites.MakePublic<Action<int>>(GenericMethod);
    }
}
