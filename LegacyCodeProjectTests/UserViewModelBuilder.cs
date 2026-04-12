using LegacyCodeProject.Core;
using LegacyCodeProject.Viewmodels;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace LegacyCodeProjectTests;

[GeneratorInstructions]
public class UserViewModelBuilder : IGeneratorInstructions
{
    public void Configure()
    {
        Overwrites.ForClass<UserViewModel>();

        Overwrites.Include<BaseBuilder>();

        Overwrites.ReplaceProperty<Func<DateTime>>(() => DateTime.Now, () => TestClock.Now);

        Overwrites.Replace(File.ReadAllText, TestFile.ReadAllText);

        Overwrites.Replace(s => s.IsValidEmail(), TestEmail.IsValidEmail);

        Overwrites.Mock<UserService>(); // Automatically create a fake implementation using an interface, UserService => IUserService, and redirect calls to the fake

        Overwrites.MockInheritance(); // Automatically create a fake base class if needed, could be combined with InheritFrom?

        Overwrites.InheritFrom<FakeBaseViewModel>(); // Developer provided fake base, usefull for base classes that are used a lot

        // Or make it mockable:
        Overwrites.Mockable<Func<string, string>>(File.ReadAllText);

        Overwrites.Mockable(() => DateTime.Now);

        // Testing some different designs
        Overwrites.MakePublic(nameof(IShadow.OnLoad)); // Not great
        //Overwrites.MakePublic(IShadow.OnLoad); // A bit better, at least the overload is resolved by the compiler, but still not great
        Overwrites.MakePublic<IShadow, Action<object?, EventArgs>>(x => x.OnLoad); // Better, show signature. Generator could notify/error if no matches found
        Overwrites.MakePublic("OnLoad"); // Ew
        Overwrites.MakePublic<UserViewModel, Action<UserViewModel, object?, EventArgs>>((vm, sender, e) => UserViewModelAccessors.OnLoad(vm, sender, e)); // Most accurate but very difficult
        Overwrites.MakePublic(UserViewModelAccessors.OnLoad); // Slightly better than above

        //[UnsafeAccessorType(nameof(UserViewModel))]
        // Apparantly I could use https://github.com/pardeike/Harmony as well (runtime method monkey patching)
    }

    private interface IShadow
    {
        public void OnLoad(object? sender, EventArgs e);
    }

    internal static class UserViewModelAccessors // I am probably not going to use this design, way too difficult for a developer to use
    {
        // Name omitted: by default, the accessor method name "OnLoad"
        // is used as the target member name.
        [UnsafeAccessor(UnsafeAccessorKind.Method)]
        internal static extern void OnLoad(UserViewModel @this, object? sender, EventArgs e);
    }
}

public class BaseBuilder : IGeneratorInstructions
{
    public void Configure()
    {
        Overwrites.ReplaceProperty<Func<DateTime>>(() => DateTime.Now, () => TestClock.Now);
        Overwrites.Replace(File.ReadAllText, TestFile.ReadAllText);
        Overwrites.Replace(s => s.IsValidEmail(), TestEmail.IsValidEmail);
    }
}

public class Overwrites
{
    public static void ReplaceProperty<TDelegate>(Expression<TDelegate> target, Expression<TDelegate> replacement)
        where TDelegate : Delegate { }

    public static void Replace<TDelegate>(TDelegate target, TDelegate replacement)
        where TDelegate : Delegate { }

    public static void MakePublic<TTarget, TDelegate>(Expression<TDelegate> selector)
        where TDelegate : Delegate { }

    public static void MakePublic<TDelegate>(TDelegate accessor)
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

    // Etc. for the others

    // public static void MakePublic(string methodName, params Type[] parameterTypes) { }

    internal static void ForClass<T>()
    {
        throw new NotImplementedException();
    }

    internal static void MockInheritance()
    {
        throw new NotImplementedException();
    }

    internal static void InheritFrom<T>()
    {
        throw new NotImplementedException();
    }

    public static void RedirectNew<T1, T2>()
    {
        throw new NotImplementedException();
    }
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

public static class TestFile
{
    public static string ReadAllText(string path) => "Test content";
}

public static class TestEmail
{
    public static bool IsValidEmail(string email) => true;
}
