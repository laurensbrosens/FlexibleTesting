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
        var vm = new UserViewModel(data);

        // Assert
        Assert.That(vm.Name, Is.EqualTo("Default"));
    }

    [Test]
    public void OnLoad_sets_Name_to_Test()
    {
        // Arrange
        var data = new SomeDataObject();
        var vm = new UserViewModel(data);
        vm.Name = "Before";

        // Act
        vm.OnLoad(null, EventArgs.Empty);

        // Assert
        Assert.That(vm.Name, Is.EqualTo("Test"));
    }

    [Test]
    public void Changing_SomeDataObject_property_triggers_LoadEvent()
    {
        // Arrange
        var data = new SomeDataObject();
        var vm = new UserViewModel(data);
        
        bool loadEventFired = false;
        vm.LoadEvent += (sender, e) => loadEventFired = true;

        // Act - Change a property in SomeDataObject
        data.MyProperty = 42;

        // Assert
        Assert.That(loadEventFired, Is.True);
    }
}
