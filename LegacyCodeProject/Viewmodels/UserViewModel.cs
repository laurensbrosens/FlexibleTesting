using LegacyCodeProject.Core;
using System.Runtime.CompilerServices;
using static LegacyCodeProject.Viewmodels.UserViewModel_g;

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

    private UserService _userService; // This is the kind of thing I want to avoid in tests, maybe the source generator can move this to the generated class and make it mockable?
}

// Example of what the generated class would look like:
public class UserViewModel_g : BaseViewModel_g //: BaseViewModel
{
    // Auto-generated new members:
    private readonly IAutoDependencies _dependencies;

    public UserViewModel_g(
        SomeDataObject someDataObject,
        IAutoDependencies dependencies,
        IAutoDependenciesBase baseDependencies
    )
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
            _dependencies.OnPropertyChanged(); // Huh, I did not think about that, maybe the source generator can copy [CallerMemberName] over?
        }
    }

    public DateTime DateTime { get; set; }

    protected override void OnLoad(object? sender, EventArgs e) // Remove override or create virtual in base class? Should be a virtual method in BaseViewModel_g that calls IAutoDependenciesBase.OnLoad
    {
        base.OnLoad(sender, e); // Of _dependencies.OnLoad(sender, e);?
        Name = "Test";
    }

    private IUserService _userService;

    public interface IAutoDependencies
    {
        Func<DateTime> Now { get; }
        IUserService UserService { get; }
        void OnPropertyChanged([CallerMemberName] string propertyName = null!); // Is this possible for a sourcegenerator?
    }

    public interface IAutoDependenciesBase
    {
        void OnLoad(object? sender, EventArgs e);
    }
}

public interface IUserService
{
}

public class BaseViewModel_g(SomeDataObject someDataObject, IAutoDependenciesBase dependencies)
{
    protected virtual void OnLoad(object? sender, EventArgs e)
    {
        dependencies.OnLoad(sender, e);
    }
}
