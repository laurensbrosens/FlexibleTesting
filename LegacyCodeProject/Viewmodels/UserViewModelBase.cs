using LegacyCodeProject.Core;
using LegacyCodeProjectCore;

namespace LegacyCodeProject.Viewmodels;

public class UserViewModelBase : ViewModelCore
{
    public UserViewModelBase(SomeDataObject someDataObject) : base("test")
    {
        SomeDataObject = someDataObject;
        Name = "Base";
        CreatedAt = DateTime.Now;
        _userService = new UserService();
        SomeMethod();
    }

    public SomeDataObject SomeDataObject { get; }

    public string Name { get; set; }

    public string Summary => $"{Name} ({_userService.GetUserName(Name)})";

    private UserService _userService;
}
