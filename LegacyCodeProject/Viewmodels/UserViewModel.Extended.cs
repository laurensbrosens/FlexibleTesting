using LegacyCodeProject.Core;

namespace LegacyCodeProject.Viewmodels;

public partial class UserViewModel
{
    public string ExtendedProperty { get; set; } = "ExtendedDefault";

    public void ExtendedMethod()
    {
        Token = Token + "-extended";
    }
}
