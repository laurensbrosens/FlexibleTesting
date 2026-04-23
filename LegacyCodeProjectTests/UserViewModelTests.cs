namespace LegacyCodeProjectTests;

using LegacyCodeProject.Core;
using LegacyCodeProject.Viewmodels;
using LegacyCodeProjectCore;
using NSubstitute;

public class UserViewModelTests
{
    [Test]
    public void BaseConstructor_WhenInitialized_ShouldApplyRecursiveBaseRewrites()
    {
        var data = new SomeDataObject();
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();
        var coreDeps = Substitute.For<IAutoViewModelCoreDependencies>();
        var expectedDate = new DateTime(2026, 4, 15);
        var expectedService = Substitute.For<IAutoUserService>();

        baseDeps.Now.Returns(() => expectedDate);
        baseDeps.UserService().Returns(expectedService);
        coreDeps.Now.Returns(() => expectedDate);
        expectedService.GetUserName(Arg.Any<string>()).Returns("base-user");

        var vm = new UserViewModelBase_G(data, baseDeps, coreDeps);

        Assert.Multiple(() =>
        {
            Assert.That(vm, Is.TypeOf<UserViewModelBase_G>());
            Assert.That(vm.Name, Is.EqualTo("Base"));
            Assert.That(vm.CreatedAt, Is.EqualTo(expectedDate));
            Assert.That(vm.SomeDataObject, Is.SameAs(data));
            Assert.That(vm.Summary, Is.EqualTo("Base (base-user)"));
        });
    }

    [Test]
    public void DerivedConstructor_WhenInitialized_ShouldInheritFromGeneratedBase()
    {
        var data = new SomeDataObject();
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        var coreDeps = Substitute.For<IAutoViewModelCoreDependencies>();
        var expectedDate = new DateTime(2026, 4, 16);
        var expectedGuid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var expectedService = Substitute.For<IAutoUserService>();

        baseDeps.Now.Returns(() => expectedDate);
        baseDeps.UserService().Returns(expectedService);
        coreDeps.Now.Returns(() => expectedDate);
        deps.NewGuid().Returns(expectedGuid);
        deps.Now.Returns(() => expectedDate);
        expectedService.GetUserName(Arg.Any<string>()).Returns("base-user");

        var vm = new UserViewModel_G<string>(data, deps, baseDeps, coreDeps);

        Assert.Multiple(() =>
        {
            Assert.That(vm, Is.InstanceOf<UserViewModelBase_G>());
            Assert.That(vm.Name, Is.EqualTo("Base"));
            // Note: vm.CreatedAt refers to the hidden property in UserViewModel_G<T>, 
            // but the base constructor sets the one in ViewModelCore_G.
            // We cast to base to verify it was set correctly by the base constructor.
            Assert.That(((UserViewModelBase_G)vm).CreatedAt, Is.EqualTo(expectedDate));
            Assert.That(vm.Token, Is.EqualTo(expectedGuid.ToString()));
        });
    }

    [Test]
    public void MergedPartialMembers_ShouldBeAccessibleAndFunctional()
    {
        var data = new SomeDataObject();
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        var coreDeps = Substitute.For<IAutoViewModelCoreDependencies>();
        var expectedDate = new DateTime(2026, 1, 1);

        deps.Now.Returns(() => expectedDate);

        var vm = new UserViewModel_G<string>(data, deps, baseDeps, coreDeps);

        Assert.Multiple(() =>
        {
            Assert.That(vm.ExtendedProperty, Is.EqualTo("ExtendedDefault"));
            
            vm.ExtendedMethod();
            
            Assert.That(vm.CreatedAt, Is.EqualTo(expectedDate));
            Assert.That(vm.Token, Does.EndWith("-extended"));
        });
    }

    [Test]
    public void RecursiveBaseAndDerivedDependencies_ShouldStaySeparated()
    {
        var data = new SomeDataObject();
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        var coreDeps = Substitute.For<IAutoViewModelCoreDependencies>();
        var expectedGuid = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var expectedService = Substitute.For<IAutoUserService>();

        baseDeps.Now.Returns(() => new DateTime(2026, 4, 17));
        baseDeps.UserService().Returns(expectedService);
        coreDeps.Now.Returns(() => new DateTime(2026, 4, 17));
        deps.NewGuid().Returns(expectedGuid);
        expectedService.GetUserName(Arg.Any<string>()).Returns("base-user");

        var vm = new UserViewModel_G<string>(data, deps, baseDeps, coreDeps);

        Assert.Multiple(() =>
        {
            Assert.That(vm.Summary, Is.EqualTo("Base (base-user)"));
            Assert.That(vm.Token, Is.EqualTo(expectedGuid.ToString()));
            baseDeps.Received(1).UserService();
            deps.Received(1).NewGuid();
        });
    }

    [Test]
    public void GenericMethod_WhenCalled_ShouldUpdateToken()
    {
        var data = new SomeDataObject();
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        var coreDeps = Substitute.For<IAutoViewModelCoreDependencies>();

        var vm = new UserViewModel_G<string>(data, deps, baseDeps, coreDeps);

        vm.GenericMethod(123);
        Assert.That(vm.Token, Is.EqualTo("123"));

        vm.GenericMethod("hello");
        Assert.That(vm.Token, Is.EqualTo("hello"));

        vm.GenericMethod<object>(null);
        Assert.That(vm.Token, Is.EqualTo("null"));
    }
}
