using System.Threading;
using System.Threading.Tasks;

namespace LegacyTestability;

public abstract class HeavyBaseViewModel_Fake : NotifyBase
{
    private string _title = "";

    public string? LastNavigationTarget { get; private set; }

    public virtual Task OnLoadedAsync(CancellationToken ct) => Task.CompletedTask;

    public virtual void Navigate(string target) => LastNavigationTarget = target;

    public virtual string Title
    {
        get => _title;
        set
        {
            if (_title == value)
                return;
            _title = value;
            Raise();
        }
    }
}