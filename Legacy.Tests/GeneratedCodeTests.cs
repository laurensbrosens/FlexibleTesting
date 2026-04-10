using NUnit.Framework;
using Legacy.App;
using MyTests.Generation; // To ensure MyLegacyRules is compiled

namespace Legacy.Tests;

public class GeneratedCodeTests
{
    [Test]
    public void CustomerViewModel_TestClass_IsGenerated()
    {
        // This test doesn't actually run code from the generated class,
        // but its mere compilation (and the fact that this test project builds)
        // indicates that the source generator has run and produced the type.

        // We can try to get the type by name to assert its existence.
        // This relies on the generated class being in the same assembly as CustomerViewModel.
        var generatedType = typeof(CustomerViewModel).Assembly.GetType("Legacy.App.CustomerViewModel_TestClass");
        
        Assert.That(generatedType, Is.Not.Null, "CustomerViewModel_TestClass was not generated.");

        // Optionally, check for injected members
        var testHookMethod = generatedType?.GetMethod("__TestHook");
        Assert.That(testHookMethod, Is.Not.Null, "__TestHook method was not injected.");
        Assert.That(testHookMethod?.Invoke(Activator.CreateInstance(generatedType), null), Is.EqualTo("ok"), "__TestHook did not return 'ok'.");
    }
}
