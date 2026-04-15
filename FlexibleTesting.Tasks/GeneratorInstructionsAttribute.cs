using System;

namespace FlexibleTesting;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class GeneratorInstructionsAttribute : Attribute
{
    public Type[] InterfacesTypes { get; }
    public GeneratorInstructionsAttribute(params Type[] interfacesTypes)
    {
        InterfacesTypes = interfacesTypes;
    }
}