namespace LegacyCodeProjectTests;

using LegacyCodeProject.Core;
using LegacyCodeProject.Viewmodels;
using NSubstitute;

public class UserViewModelTests
{
    [Test]
    public void Constructor_WhenInitialized_ShouldSetDefaultName()
    {
        // Arrange
        var data = new SomeDataObject();
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        deps.Now.Returns(() => DateTime.Now);
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();

        // Act
        var vm = new UserViewModel_G(data, deps, baseDeps);

        // Assert
        Assert.That(vm.Name, Is.EqualTo("Default"));
    }

    [Test]
    public void Constructor_WhenInitialized_ShouldSetDateTimeFromDependency()
    {
        // Arrange
        var data = new SomeDataObject();
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        var expectedDate = new DateTime(2026, 4, 15);
        deps.Now.Returns(() => expectedDate);
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();

        // Act
        var vm = new UserViewModel_G(data, deps, baseDeps);

        // Assert
        Assert.That(vm.DateTime, Is.EqualTo(expectedDate));
    }

    [Test]
    public void NameProperty_WhenSet_ShouldInvokeOnPropertyChanged()
    {
        // Arrange
        var data = new SomeDataObject();
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        deps.Now.Returns(() => DateTime.Now);
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();
        var vm = new UserViewModel_G(data, deps, baseDeps);

        // Act
        vm.Name = "Changed Name";

        // Assert
        baseDeps.Received().OnPropertyChanged("Name");
    }

    [Test]
    public void SomePrivateMethod_WhenCalled_ShouldUpdateNameProperty()
    {
        // Arrange
        var data = new SomeDataObject();
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        deps.Now.Returns(() => DateTime.Now);
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();
        var vm = new UserViewModel_G(data, deps, baseDeps);

        // Act
        vm.SomePrivateMethod();

        // Assert
        Assert.That(vm.Name, Is.EqualTo("Something to test"));
    }

    [Test]
    public void Constructor_Sets_InitialValues_And_Mocks_StaticCalls()
    {
        // Arrange
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();
        var data = new SomeDataObject();
        var expectedDate = new DateTime(2026, 4, 15);

        deps.Now.Returns(() => expectedDate);

        // Act
        var vm = new UserViewModel_G(data, deps, baseDeps);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(vm.Name, Is.EqualTo("Default"));
            Assert.That(vm.DateTime, Is.EqualTo(expectedDate));
        });
    }

    [Test]
    public void Setting_Name_Triggers_OnPropertyChanged_On_BaseDependencies()
    {
        // Arrange
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();
        var data = new SomeDataObject();
        deps.Now.Returns(() => DateTime.Now);
        var vm = new UserViewModel_G(data, deps, baseDeps);

        // Act
        vm.Name = "Changed Name";

        // Assert
        baseDeps.Received().OnPropertyChanged("Name");
    }

    [Test]
    public void Constructor_Calls_OnPropertyChanged()
    {
        // Arrange
        var data = new SomeDataObject();
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        deps.Now.Returns(() => DateTime.Now);
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();

        // Act
        var vm = new UserViewModel_G(data, deps, baseDeps);

        // Assert
        // Constructor calls OnPropertyChanged() at the end
        baseDeps.Received().OnPropertyChanged(Arg.Any<string>());
    }

    [Test]
    public void OnPropertyChanged_Is_Mockable_Through_BaseDependencies()
    {
        // Arrange
        var data = new SomeDataObject();
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        deps.Now.Returns(() => DateTime.Now);
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();
        var vm = new UserViewModel_G(data, deps, baseDeps);

        baseDeps.ClearReceivedCalls();

        // Act
        vm.OnPropertyChanged("TestProperty");

        // Assert
        baseDeps.Received(1).OnPropertyChanged("TestProperty");
    }

    [Test]
    public void NameProperty_Get_Also_Triggers_OnPropertyChanged()
    {
        // Arrange
        var data = new SomeDataObject();
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        deps.Now.Returns(() => DateTime.Now);
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();
        var vm = new UserViewModel_G(data, deps, baseDeps);

        baseDeps.ClearReceivedCalls();

        // Act
        var name = vm.Name;

        // Assert
        // The getter calls OnPropertyChanged
        baseDeps.Received().OnPropertyChanged(Arg.Any<string>());
        Assert.That(name, Is.EqualTo("Default"));
    }
}
