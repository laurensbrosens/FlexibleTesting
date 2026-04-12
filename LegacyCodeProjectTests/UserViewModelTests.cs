using LegacyCodeProject.Core;
using LegacyCodeProject.Viewmodels;

namespace LegacyCodeProjectTests;

public class UserViewModelTests
{
    [Test]
    public void Constructor_sets_DefaultName()
    {
        // Arrange
        var data = new SomeDataObject();

        // Act
        var vm = new UserViewModel_G(data);

        // Assert
        Assert.That(vm.Name, Is.EqualTo("Default"));
    }

    [Test]
    public void OnLoad_sets_Name_to_Test()
    {
        // Arrange
        var data = new SomeDataObject();
        var vm = new UserViewModel_G(data);
        vm.Name = "Before";

        // Act
        //vm.OnLoad(null, EventArgs.Empty);

        // Assert
        Assert.That(vm.Name, Is.EqualTo("Test"));
    }

    [Test]
    public void Changing_SomeDataObject_property_triggers_LoadEvent()
    {
        // Arrange
        var data = new SomeDataObject();
        var vm = new UserViewModel_G(data);
        
        bool loadEventFired = false;
        //vm.LoadEvent += (sender, e) => loadEventFired = true;

        // Act - Change a property in SomeDataObject
        data.MyProperty = 42;

        // Assert
        Assert.That(loadEventFired, Is.True);
    }
}
/* Old code


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
*/
