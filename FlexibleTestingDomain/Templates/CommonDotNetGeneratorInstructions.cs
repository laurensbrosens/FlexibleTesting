using System;

namespace FlexibleTestingDomain.Templates;

/// <summary>
/// Common generator-only seams for nondeterministic .NET functionality.
/// The generator consumes this template through Overwrites.Include{T}().
/// </summary>
[GeneratorInstructionsTemplate]
public sealed class CommonDotNetGeneratorInstructions : IGeneratorInstructions
{
    public void Configure()
    {
        Overwrites.Mock(() => DateTime.Now);
        Overwrites.Mock(() => DateTime.UtcNow);
        Overwrites.Mock(() => Guid.NewGuid());
    }
}
