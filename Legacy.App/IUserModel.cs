using System.ComponentModel;

namespace Legacy.App;

public interface IUserModel : INotifyPropertyChanged
{
    string Name { get; }
}