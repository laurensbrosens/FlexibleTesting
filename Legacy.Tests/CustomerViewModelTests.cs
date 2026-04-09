using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Legacy.App;
using Legacy.Tests.SutCopy;
using Legacy.Tests.TestDoubles;
using NSubstitute;
using NUnit.Framework;

namespace Legacy.Tests;

[TestFixture]
public sealed class CustomerViewModelTests
{
    [SetUp]
    public void SetUp()
    {
        TestClock.Now = new System.DateTime(2020, 1, 2, 3, 4, 5, System.DateTimeKind.Utc);
        TestFile.Clear();
    }

    [Test]
    public void Updates_DisplayName_When_User_Name_Changes()
    {
        var user = Substitute.For<IUserModel>();
        user.Name.Returns("Alice");

        var vm = new CustomerViewModel_TestClass(user);
        Assert.That(vm.DisplayName, Is.EqualTo("Customer: Alice"));

        user.Name.Returns("Bob");
        user.PropertyChanged += Raise.Event<PropertyChangedEventHandler>(user, new PropertyChangedEventArgs(nameof(IUserModel.Name)));

        Assert.That(vm.DisplayName, Is.EqualTo("Customer: Bob"));
    }

    [Test]
    public async Task OnLoadedAsync_Sets_Title_And_Navigates()
    {
        var user = Substitute.For<IUserModel>();
        user.Name.Returns("Alice");

        TestFile.SetText("app.mode", "go");

        var vm = new CustomerViewModel_TestClass(user);

        await vm.OnLoadedAsync(CancellationToken.None);

        Assert.That(vm.Title, Is.EqualTo("Customer (go)"));
        Assert.That(vm.LastNavigationTarget, Is.EqualTo("Orders"));
    }

    [Test]
    public void Email_Validation_Uses_TestEmail()
    {
        var user = Substitute.For<IUserModel>();
        user.Name.Returns("Alice");

        var vm = new CustomerViewModel_TestClass(user);

        vm.Email = "not-an-email";
        Assert.That(vm.Status, Is.EqualTo("Invalid"));

        vm.Email = "a@b.com";
        Assert.That(vm.Status, Is.EqualTo("OK"));
    }
}