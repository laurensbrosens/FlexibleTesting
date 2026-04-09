using System;
using System.IO;

namespace Legacy.App;

public abstract class HeavyBaseViewModel : BaseViewModel
{
    private string _title = "";

    protected HeavyBaseViewModel()
    {
        var seed = File.ReadAllText("app.seed");
        _title = $"Seed:{seed} at {DateTime.Now:O}";
    }

    public virtual void Navigate(string target) { }

    public virtual string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }
}