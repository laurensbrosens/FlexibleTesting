using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Legacy.App;

public sealed class CustomerViewModel : HeavyBaseViewModel
{
    private readonly IUserModel _user;
    private string _displayName = "";
    private string _email = "";
    private string _status = "";

    public CustomerViewModel(IUserModel user)
    {
        _user = user;
        _user.PropertyChanged += UserOnPropertyChanged;
        UpdateDisplayName();
    }

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public string Email
    {
        get => _email;
        set
        {
            if (SetProperty(ref _email, value))
                Status = value.IsValidEmail() ? "OK" : "Invalid";
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public override Task OnLoadedAsync(CancellationToken ct)
    {
        var mode = File.ReadAllText("app.mode").Trim();
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
