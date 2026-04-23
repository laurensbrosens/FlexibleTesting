using LegacyCodeProject.Core;
using NUnit.Framework;

namespace LegacyCodeProjectTests;

public class SealedClassTests
{
    [Test]
    public void GeneratedClass_ShouldNotBeSealed()
    {
        var type = typeof(SealedClass_G);
        Assert.That(type.IsSealed, Is.False, "The generated class should not be sealed.");
    }
}
