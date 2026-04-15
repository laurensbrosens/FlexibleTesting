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

        // Act
        var vm = new UserViewModel_G(data, deps);

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

        // Act
        var vm = new UserViewModel_G(data, deps);

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
        var vm = new UserViewModel_G(data, deps);

        // Act
        vm.Name = "Changed Name";

        // Assert
        deps.Received().OnPropertyChanged("Name");
    }

    [Test]
    public void SomePrivateMethod_WhenCalled_ShouldUpdateNameProperty()
    {
        // Arrange
        var data = new SomeDataObject();
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        deps.Now.Returns(() => DateTime.Now);
        var vm = new UserViewModel_G(data, deps);

        // Act
        vm.SomePrivateMethod();

        // Assert
        Assert.That(vm.Name, Is.EqualTo("Something to test"));
    }
    /* 
    // Commented out until inheritance mocking functionality is implemented
    
    [Test]
    public void Constructor_Sets_InitialValues_And_Mocks_StaticCalls()
    {
        // Arrange
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        var baseDeps = Substitute.For<IAutoBaseViewModelDependencies>();
        var data = new SomeDataObject();
        var expectedDate = new DateTime(2026, 4, 15);
        
        deps.Now().Returns(expectedDate);

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
    public void Setting_Name_Triggers_OnPropertyChanged_On_Dependencies()
    {
        // Arrange
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        var baseDeps = Substitute.For<IAutoBaseViewModelDependencies>();
        var data = new SomeDataObject();
        var vm = new UserViewModel_G(data, deps, baseDeps);

        // Act
        vm.Name = "Changed Name";

        // Assert
        deps.Received().OnPropertyChanged("Name");
    }

    [Test]
    public void OnLoad_Executes_SomePrivateMethod_Logic_Inheritance()
    {
        // Arrange
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        var baseDeps = Substitute.For<IAutoBaseViewModelDependencies>();
        var data = new SomeDataObject();
        var vm = new UserViewModel_G(data, deps, baseDeps);

        // Act
        vm.OnLoad(null, EventArgs.Empty);

        // Assert
        Assert.That(vm.Name, Is.EqualTo("Something to test"));
    }

    [Test]
    public void SomePrivateMethod_Calls_Mocked_UserService_With_Correct_Data_Inheritance()
    {
        // Arrange
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        var baseDeps = Substitute.For<IAutoBaseViewModelDependencies>();
        var data = new SomeDataObject();
        var userServiceMock = Substitute.For<IUserService>(); 
        
        deps.UserService().Returns(userServiceMock);
        
        var vm = new UserViewModel_G(data, deps, baseDeps);

        // Act
        vm.SomePrivateMethod();

        // Assert
        userServiceMock.Received().GetUserName("Something to test");
    }
    */
}
