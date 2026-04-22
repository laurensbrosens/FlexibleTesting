namespace LegacyCodeProjectTests;

using LegacyCodeProject.Core;
using LegacyCodeProject.Viewmodels;
using NSubstitute;

public class UserViewModelTests
{
    [Test]
    public void BaseConstructor_WhenInitialized_ShouldApplyRecursiveBaseRewrites()
    {
        var data = new SomeDataObject();
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();
        var expectedDate = new DateTime(2026, 4, 15);
        var expectedService = Substitute.For<IAutoUserService>();

        baseDeps.Now.Returns(() => expectedDate);
        baseDeps.UserService().Returns(expectedService);
        expectedService.GetUserName(Arg.Any<string>()).Returns("base-user");

        var vm = new UserViewModelBase_G(data, baseDeps);

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
        var expectedDate = new DateTime(2026, 4, 16);
        var expectedGuid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var expectedService = Substitute.For<IAutoUserService>();

        baseDeps.Now.Returns(() => expectedDate);
        baseDeps.UserService().Returns(expectedService);
        deps.NewGuid().Returns(expectedGuid);
        expectedService.GetUserName(Arg.Any<string>()).Returns("base-user");

        var vm = new UserViewModel_G(data, deps, baseDeps);

        Assert.Multiple(() =>
        {
            Assert.That(vm, Is.InstanceOf<UserViewModelBase_G>());
            Assert.That(vm.Name, Is.EqualTo("Base"));
            Assert.That(vm.CreatedAt, Is.EqualTo(expectedDate));
            Assert.That(vm.Token, Is.EqualTo(expectedGuid.ToString()));
        });
    }

    [Test]
    public void RecursiveBaseAndDerivedDependencies_ShouldStaySeparated()
    {
        var data = new SomeDataObject();
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        var expectedGuid = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var expectedService = Substitute.For<IAutoUserService>();

        baseDeps.Now.Returns(() => new DateTime(2026, 4, 17));
        baseDeps.UserService().Returns(expectedService);
        deps.NewGuid().Returns(expectedGuid);
        expectedService.GetUserName(Arg.Any<string>()).Returns("base-user");

        var vm = new UserViewModel_G(data, deps, baseDeps);

        Assert.Multiple(() =>
        {
            Assert.That(vm.Summary, Is.EqualTo("Base (base-user)"));
            Assert.That(vm.Token, Is.EqualTo(expectedGuid.ToString()));
            baseDeps.Received(1).UserService();
            deps.Received(1).NewGuid();
        });
    }
}
