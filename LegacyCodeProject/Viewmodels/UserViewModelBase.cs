using LegacyCodeProject.Core;

namespace LegacyCodeProject.Viewmodels;

public class UserViewModelBase
{
    public UserViewModelBase(SomeDataObject someDataObject)
    {
        SomeDataObject = someDataObject;
        Name = "Base";
        CreatedAt = DateTime.Now;
        _userService = new UserService();
    }

    public SomeDataObject SomeDataObject { get; }

    public string Name { get; set; }

    public DateTime CreatedAt { get; set; }

    public string Summary => $"{Name} ({_userService.GetUserName(Name)})";

    private UserService _userService;
}
