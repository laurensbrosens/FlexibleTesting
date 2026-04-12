using LegacyCodeProject.Core;
using LegacyCodeProject.Viewmodels;
using System.Linq.Expressions;

namespace LegacyCodeProjectTests;

[GeneratorInstructions]
public class UserViewModelBuilder : IGeneratorInstructions
{
    public void Configure()
    {
        Overwrites.ForClass<UserViewModel>();
        Overwrites.Include<BaseBuilder>();
        Overwrites.Mock<UserService>(); // Automatically create a fake implementation using an interface, UserService => IUserService, and redirect calls to the fake
        Overwrites.MockInheritance(); // Automatically create a fake base class if needed, could be combined with InheritFrom?
        Overwrites.InheritFrom<FakeBaseViewModel>(); // Developer provided fake base, usefull for base classes that are used a lot
        Overwrites.Mockable(() => DateTime.Now);
        Overwrites.MakePublic<IShadow, Action<object?, EventArgs>>(x => x.OnLoad);
        // Overwrites.MakePublic("OnLoad");
    }

    private interface IShadow
    {
        public void OnLoad(object? sender, EventArgs e);
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

public class Overwrites
{
    public static void ReplaceProperty<TDelegate>(Expression<TDelegate> target, Expression<TDelegate> replacement)
        where TDelegate : Delegate { }

    public static void Replace<TDelegate>(TDelegate target, TDelegate replacement)
        where TDelegate : Delegate { }

    public static void Mockable<TDelegate>(TDelegate value)
        where TDelegate : Delegate { }

    public static void MakePublic<TInterface, TDelegate>(Expression<Func<TInterface, TDelegate>> methodSelector)
        where TDelegate : Delegate { }

    public static void MakePublic(string methodName, params Type[] parameterTypes) { }

    public static void Include<T>()
        where T : IGeneratorInstructions { }

    public static void RedirectNew<TTarget, TDelegate>(Func<TTarget> value1, Func<TDelegate> value2)
        where TDelegate : Delegate { }

    public static void Mock<TClass>() { }

    public static void MockWithInterface<TClass, TInterface>() { }

    public static void ForClass<TClass>() { }

    public static void MockInheritance() { }

    public static void InheritFrom<TClass>() { }
}

public static class StringExtensions
{
    public static bool IsValidEmail(this string s) => TestEmail.IsValidEmail(s);
}

public class FakeBaseViewModel { }

public class GeneratorInstructionsAttribute : Attribute { }

public interface IGeneratorInstructions
{
    void Configure();
}

public static class TestClock
{
    public static DateTime Now => new DateTime(2000, 1, 1);
}

public static class TestEmail
{
    public static bool IsValidEmail(string email) => true;
}
