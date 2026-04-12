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
    }

    public string Name
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    public DateTime DateTime { get; set; }

    protected override void OnLoad(object? sender, EventArgs e)
    {
        base.OnLoad(sender, e);
        IReallyWontToTestThisMethod();
    }

    private UserService _userService;

    private void IReallyWontToTestThisMethod()
    {
        Name = "Something to test";
    }
}