using LegacyCodeProject.Core;

namespace LegacyCodeProject.Viewmodels;

public class UserViewModel : UserViewModelBase
{
    public UserViewModel(SomeDataObject someDataObject)
        : base(someDataObject)
    {
        Token = Guid.NewGuid().ToString();
    }

    public string Token { get; set; }
}
