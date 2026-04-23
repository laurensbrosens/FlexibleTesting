using FlexibleTestingDomain;
using LegacyCodeProject.Core;

namespace LegacyCodeProjectTests;

[GeneratorInstructions(typeof(SealedClass))]
public class SealedClassBuilder : IGeneratorInstructions
{
    public void Configure()
    {
        Overwrites.ForClass<SealedClass>();
        Overwrites.RemoveSealed();
    }
}
