using LegacyCodeProject.Core;

namespace LegacyCodeProject.Viewmodels;

public class UserViewModel : BaseViewModel
{
    public UserViewModel(SomeDataObject someDataObject)
        : base(someDataObject)
    {
        Name = "Default";
        DateTime = DateTime.Now; // Test
        _userService = new UserService();
        OnPropertyChanged();
    }
    public DateTime Now { get; set; }

    public string Name
    {
        get
        {
            OnPropertyChanged();
            return field;
        }
        set
        {
            DateTime = DateTime.Now; // Test
            field = value;
            OnPropertyChanged();
        }
    }

    public DateTime DateTime { get; set; }

    protected override void OnLoad(object? sender, EventArgs e)
    {
        base.OnLoad(sender, e);
        SomePrivateMethod();
        OnPropertyChanged();
    }

    private UserService _userService;

    private void SomePrivateMethod()
    {
        Name = "Something to test";
        _userService.GetUserName(Name);
    }
}