using LegacyCodeProject.Core;
using System.Runtime.CompilerServices;

namespace LegacyCodeProject.Viewmodels;

public class UserViewModel : BaseViewModel
{
    public UserViewModel(SomeDataObject someDataObject)
        : base(someDataObject)
    {
        Name = "Default";
        DateTime = DateTime.Now;
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
        Name = "Test";
    }

    private UserService _userService;
}
/*
// Example of what the generated class would look like:
public class UserViewModel_g : BaseViewModel_g //: BaseViewModel
{
    // Auto-generated new members:
    private readonly IAutoDependencies _dependencies;

    public UserViewModel_g(SomeDataObject someDataObject, IAutoDependencies dependencies, IAutoDependenciesBase baseDependencies)
        : base(someDataObject, baseDependencies)
    {
        _dependencies = dependencies;
        Name = "Default";
        DateTime = _dependencies.Now();
        _userService = _dependencies.UserService;
    }

    public string Name
    {
        get;
        set
        {
            field = value;
            _dependencies.OnPropertyChanged();
        }
    }

    public DateTime DateTime { get; set; }

    protected override void OnLoad(object? sender, EventArgs e)
    {
        base.OnLoad(sender, e); // Or _dependencies.OnLoad(sender, e);?
        Name = "Test";
    }

    private IUserService _userService;


}
*/    
public interface IAutoDependencies
    {
        Func<DateTime> Now { get; }
        IUserService UserService { get; }
        void OnPropertyChanged([CallerMemberName] string propertyName = null!); // Note, the generator has to check for [CallerMemberName]!
    }

    public interface IAutoDependenciesBase
    {
        void OnLoad(object? sender, EventArgs e);
    }
public interface IUserService { }

public class BaseViewModel_g(SomeDataObject someDataObject, IAutoDependenciesBase dependencies)
{
    protected virtual void OnLoad(object? sender, EventArgs e)
    {
        dependencies.OnLoad(sender, e);
    }
}
