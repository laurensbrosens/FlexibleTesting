using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LegacyCodeProject.Core;

public class BaseViewModel : INotifyPropertyChanged
{
    public BaseViewModel(SomeDataObject someDataObject)
    {
        SomeDataObject = someDataObject;
        LoadEvent += OnLoad;
        Console.WriteLine("Parent class side effects");
        throw new Exception("Can't run this in a unittest");
    }

    public SomeDataObject SomeDataObject
    {
        get;
        set
        {
            field = value;
            LoadEvent?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged();
        }
    }

    private event EventHandler LoadEvent;

    protected virtual void OnLoad(object? sender, EventArgs e)
    {
        Console.WriteLine("Parent class side effects");
        throw new Exception("Can't run this in a unittest");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
