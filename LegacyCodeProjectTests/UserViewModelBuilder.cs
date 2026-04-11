using LegacyCodeProject.Core;
using LegacyCodeProject.Viewmodels;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace LegacyCodeProjectTests;

[GeneratorInstructions]
public class UserViewModelBuilder//(SomeDataObject someDataObject) : UserViewModel(someDataObject), IGeneratorInstructions
{
    /*
    public class Accessor(SomeDataObject someDataObject) : UserViewModel(someDataObject)
    {
        public static string MethodProvider()
        {
            return nameof(OnLoad);
        }

        /*public static void MakePublic<TTarget, TDelegate>(Expression<TDelegate> selector)
            where TDelegate : Delegate { }*/

        public static Expression<TDelegate> MakePublic<TTarget, TDelegate>()
            where TDelegate : Delegate { 
            return (sender, e) => ((UserViewModelBuilder)null!).OnLoad(sender, e);
        }
    }*/

    public static void Configure()
    {
        Overwrites.ForClass<UserViewModel>();

        Overwrites.Replace<Func<DateTime>>(() => DateTime.Now, () => TestClock.Now);

        Overwrites.Replace<Func<string, string>>(path => File.ReadAllText(path), path => TestFile.ReadAllText(path));

        Overwrites.Replace<Func<string, bool>>(s => s.IsValidEmail(), s => TestEmail.IsValidEmail(s));

        Overwrites.RedirectNew<SomeService, Func<string, SomeService>>(); // To overwrite things like "new SomeService" that has side-effects in it's constructor, could be combined with Mockable?

        Overwrites.MockInheritance(); // Automatically create a fake base class if needed, could be combined with InheritFrom?

        Overwrites.InheritFrom<FakeBaseViewModel>(); // Developer provided fake base, usefull for base classes that are used a lot

        // Or make it mockable:
        Overwrites.Mockable<Func<string, string>>(path => File.ReadAllText(path));

        Overwrites.Mockable<Func<DateTime>>(() => DateTime.Now);

        Overwrites.MakePublic(nameof(UserViewModelBuilder.OnLoad));
        Overwrites.MakePublic(Accessor.MethodProvider());

        //[UnsafeAccessorType(nameof(UserViewModel))]

        Overwrites.MakePublic<UserViewModel, Action<UserViewModel, object?, EventArgs>>((vm, sender, e) => UserViewModelAccessors.OnLoad(vm, sender, e));
    }
    internal static class UserViewModelAccessors
    {
        // Name omitted: by default, the accessor method name "OnLoad"
        // is used as the target member name.
        [UnsafeAccessor(UnsafeAccessorKind.Method)]
        internal static extern void OnLoad(UserViewModel instance, object? sender, EventArgs e);
    }
}



internal class FakeBaseViewModel { }

internal class GeneratorInstructionsAttribute : Attribute { }

public interface IGeneratorInstructions { }

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

public static class Overwrites
{
    public static void Replace<TDelegate>(Expression<TDelegate> target, Expression<TDelegate> replacement)
        where TDelegate : Delegate { }

    // Etc. for the others

    public static void MakePublic(string methodName, params Type[] parameterTypes) { }

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

    /*
    internal static void Mockable<T>(Expression<TDelegate> value) where TDelegate : Delegate
    {
        throw new NotImplementedException();
    }
    */
    public static void MakePublic<TTarget, TDelegate>(Expression<TDelegate> selector)
        where TDelegate : Delegate { }

    internal static void RedirectNew<T1, T2>()
    {
        throw new NotImplementedException();
    }
}
