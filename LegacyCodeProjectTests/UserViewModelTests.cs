namespace LegacyCodeProjectTests;

using LegacyCodeProject.Core;
using LegacyCodeProject.Viewmodels;
using LegacyCodeProjectCore;
using NSubstitute;
using System;

public class UserViewModelTests
{
    [Test]
    public void Constructor_WhenInitialized_SetsToken()
    {
        var data = new SomeDataObject();
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();
        var coreDeps = Substitute.For<IAutoViewModelCoreDependencies>();
        var expectedGuid = Guid.NewGuid();
        deps.Guid_NewGuid().Returns(expectedGuid);

        var vm = new UserViewModel_G<string>(data, deps, baseDeps, coreDeps);

        Assert.That(vm.Token, Is.EqualTo(expectedGuid.ToString()));
    }

    [Test]
    public void Constructor_WhenInitialized_SetsNowProperty()
    {
        var data = new SomeDataObject();
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();
        var coreDeps = Substitute.For<IAutoViewModelCoreDependencies>();
        var expectedDate = new DateTime(2026, 4, 25);
        deps.DateTime_Now = expectedDate;

        var vm = new UserViewModel_G<string>(data, deps, baseDeps, coreDeps);

        Assert.That(vm.Now, Is.EqualTo(expectedDate));
    }

    [Test]
    public void GenericMethod_WhenCalledWithInt_SetsToken()
    {
        var data = new SomeDataObject();
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();
        var coreDeps = Substitute.For<IAutoViewModelCoreDependencies>();
        var vm = new UserViewModel_G<string>(data, deps, baseDeps, coreDeps);

        vm.GenericMethod(123);

        Assert.That(vm.Token, Is.EqualTo("123"));
    }

    [Test]
    public void GenericMethod_WhenCalledWithString_SetsToken()
    {
        var data = new SomeDataObject();
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();
        var coreDeps = Substitute.For<IAutoViewModelCoreDependencies>();
        var vm = new UserViewModel_G<string>(data, deps, baseDeps, coreDeps);

        vm.GenericMethod("hello");

        Assert.That(vm.Token, Is.EqualTo("hello"));
    }

    [Test]
    public void GenericMethod_WhenCalledWithNull_SetsTokenToNullString()
    {
        var data = new SomeDataObject();
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();
        var coreDeps = Substitute.For<IAutoViewModelCoreDependencies>();
        var vm = new UserViewModel_G<string>(data, deps, baseDeps, coreDeps);

        vm.GenericMethod<object>(null!);

        Assert.That(vm.Token, Is.EqualTo("null"));
    }

    [Test]
    public void ExtendedMethod_WhenCalled_AppendsSuffixToToken()
    {
        var data = new SomeDataObject();
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();
        var coreDeps = Substitute.For<IAutoViewModelCoreDependencies>();
        var vm = new UserViewModel_G<string>(data, deps, baseDeps, coreDeps);
        vm.Token = "test";

        vm.ExtendedMethod();

        Assert.That(vm.Token, Is.EqualTo("test-extended"));
    }

    [Test]
    public void ExtendedMethod_WhenCalled_UpdatesNow()
    {
        var data = new SomeDataObject();
        var deps = Substitute.For<IAutoUserViewModelDependencies>();
        var baseDeps = Substitute.For<IAutoUserViewModelBaseDependencies>();
        var coreDeps = Substitute.For<IAutoViewModelCoreDependencies>();
        var newDate = new DateTime(2026, 12, 31);
        deps.DateTime_Now = newDate;
        var vm = new UserViewModel_G<string>(data, deps, baseDeps, coreDeps);

        vm.ExtendedMethod();

        Assert.That(vm.Now, Is.EqualTo(newDate));
    }
}
