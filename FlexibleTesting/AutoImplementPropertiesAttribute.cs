using System;

namespace FlexibleTesting;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class AutoImplementPropertiesAttribute : Attribute
{
    public Type[] InterfacesTypes { get; }
    public AutoImplementPropertiesAttribute(params Type[] interfacesTypes)
    {
        InterfacesTypes = interfacesTypes;
    }
}
