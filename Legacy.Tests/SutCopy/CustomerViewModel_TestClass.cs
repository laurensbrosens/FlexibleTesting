using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Legacy.App;
using Legacy.Tests.Fakes;
using Legacy.Tests.TestDoubles;

namespace Legacy.Tests.SutCopy;

public sealed class CustomerViewModel_TestClass : HeavyBaseViewModel_Fake
{
    private readonly IUserModel _user;
    private string _displayName = "";
    private string _email = "";
    private string _status = "";

    public CustomerViewModel_TestClass(IUserModel user)
    {
        _user = user;
        _user.PropertyChanged += UserOnPropertyChanged;
        UpdateDisplayName();
    }

    public string DisplayName
    {
        get => _displayName;
        private set
        {
            if (_displayName == value)
                return;
            _displayName = value;
            Raise();
        }
    }

    public string Email
    {
        get => _email;
        set
        {
            if (_email == value)
                return;
            _email = value;
            Raise();
            Status = TestEmail.IsValidEmail(value) ? "OK" : "Invalid";
        }
    }

    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value)
                return;
            _status = value;
            Raise();
        }
    }

    public override Task OnLoadedAsync(CancellationToken ct)
    {
        var mode = TestFile.ReadAllText("app.mode").Trim();
        Title = $"Customer ({mode})";
        if (mode == "go")
            Navigate("Orders");
        return Task.CompletedTask;
    }

    private void UserOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IUserModel.Name) || string.IsNullOrEmpty(e.PropertyName))
            UpdateDisplayName();
    }

    private void UpdateDisplayName() => DisplayName = $"Customer: {_user.Name}";
}
