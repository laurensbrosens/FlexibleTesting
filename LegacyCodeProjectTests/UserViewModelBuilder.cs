using FlexibleTesting;
using LegacyCodeProject.Core;
using LegacyCodeProject.Viewmodels;

namespace LegacyCodeProjectTests;

[GeneratorInstructions]
internal class UserViewModelBuilder(SomeDataObject someDataObject) : UserViewModel(someDataObject), IGeneratorInstructions
{
    public void Configure()
    {
        Overwrites.ForClass<UserViewModel>();
        //Overwrites.Include<BaseBuilder>();
        Overwrites.Mock<UserService>(); // Automatically create a fake implementation using an interface, UserService => IUserService, and redirect calls to the fake
        Overwrites.MockInheritance(); // Automatically create a fake base class if needed, could be combined with InheritFrom?
        Overwrites.InheritFrom<FakeBaseViewModel>(); // Developer provided fake base, usefull for base classes that are used a lot
        Overwrites.Mockable(() => DateTime.Now);
        //Overwrites.Mockable(() => Now);
        Overwrites.Mockable(() => OnPropertyChanged);
        //Overwrites.Mockable(() => DateTime.Today);
        // Overwrites.MakePublic<Action<object?, EventArgs>>(() => OnLoad); // Don't need IShadow, does not work for private of course, but works for protected, internal and protected internal
        Overwrites.MakePublic<IShadow, Action>(x => x.SomePrivateMethod);
        // Overwrites.MakePublic("OnLoad"); I want to prevent allowing string based API
    }

    private interface IShadow // This is a hack for private members
    {
        public void SomePrivateMethod();
    }
}

public class BaseBuilder : IGeneratorInstructions
{
    public void Configure()
    {
        Overwrites.ReplaceProperty<Func<DateTime>>(() => DateTime.Now, () => TestClock.Now);
        Overwrites.Mockable<Func<string, string>>(File.ReadAllText);
        Overwrites.Replace(s => s.IsValidEmail(), TestEmail.IsValidEmail);
    }
}

public static class StringExtensions
{
    public static bool IsValidEmail(this string s) => TestEmail.IsValidEmail(s);
}

public class FakeBaseViewModel { }

public static class TestClock
{
    public static DateTime Now => new DateTime(2000, 1, 1);
}

public static class TestEmail
{
    public static bool IsValidEmail(string email) => true;
}
