using FlexibleTestingDomain;

namespace LegacyCodeProjectTests;

[GeneratorInstructions]
internal class StringBuilder() : IGeneratorInstructions
{
    public void Configure()
    {
        Overwrites.ForClass<string>();
    }
}
