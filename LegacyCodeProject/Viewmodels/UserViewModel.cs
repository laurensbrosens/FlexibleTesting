using LegacyCodeProject.Core;

namespace LegacyCodeProject.Viewmodels;

public class UserViewModel : BaseViewModel
{
    public UserViewModel(SomeDataObject someDataObject) : base(someDataObject)
    {
        Name = "Default";
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

    protected override void OnLoad(object? sender, EventArgs e)
    {
        base.OnLoad(sender, e);
        Name = "Test";
    }
}
