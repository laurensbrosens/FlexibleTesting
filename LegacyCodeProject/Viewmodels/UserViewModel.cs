using LegacyCodeProject.Core;

namespace LegacyCodeProject.Viewmodels;

public class UserViewModel : BaseViewModel
{
    public UserViewModel(SomeDataObject someDataObject)
        : base(someDataObject)
    {
        Name = "Default";
        DateTime = DateTime.Now; // Test
        Now = DateTime.Now;
        DateTime = Now; // Test 2
        _userService = new UserService();
        DoesThisEvenWork();
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

    public void DoesThisEvenWork()
    {
        throw new Exception("Bad side effect");
    }

    private UserService _userService;

    private void SomePrivateMethod()
    {
        Name = "Something to test";
        _userService.GetUserName(Name);
    }
}