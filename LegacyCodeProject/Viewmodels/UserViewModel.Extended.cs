using LegacyCodeProject.Core;

namespace LegacyCodeProject.Viewmodels;

public partial class UserViewModel<T>
{
    public string ExtendedProperty { get; set; } = "ExtendedDefault";

    public void ExtendedMethod()
    {
        Token = Token + "-extended";
        CreatedAt = DateTime.Now;
    }
}
