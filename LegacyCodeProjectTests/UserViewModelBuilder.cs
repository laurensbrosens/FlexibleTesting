using LegacyCodeProject.Viewmodels;
using System.Linq.Expressions;

namespace LegacyCodeProjectTests;

[GeneratorInstructions]
public class UserViewModelBuilder : IGeneratorInstructions
{
    public static void Configure()
    {
        Overwrites.ForClass<UserViewModel>();

        Overwrites.Replace<Func<DateTime>>(
            () => DateTime.Now,
            () => TestClock.Now);

        Overwrites.Replace<Func<string, string>>(
            path => File.ReadAllText(path),
            path => TestFile.ReadAllText(path));

        Overwrites.Replace<Func<string, bool>>(
            s => s.IsValidEmail(),
            s => TestEmail.IsValidEmail(s));

        Overwrites.RedirectNew<SomeService, Func<string, SomeService>>(); // To overwrite things like "new SomeService" that has side-effects in it's constructor, could be combined with Mockable?

        Overwrites.MockInheritance(); // Automatically create a fake base class if needed, could be combined with InheritFrom?

        Overwrites.InheritFrom<FakeBaseViewModel>(); // Developer provided fake base, usefull for base classes that are used a lot

        // Or make it mockable:
        Overwrites.Mockable<Func<string, string>>(path => File.ReadAllText(path));

        Overwrites.Mockable<Func<DateTime>>(() => DateTime.Now);
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
}

public static class Overwrites
{
    public static void Replace<TDelegate>(Expression<TDelegate> target, Expression<TDelegate> replacement)
        where TDelegate : Delegate
    { }
    // Etc. for the others
}