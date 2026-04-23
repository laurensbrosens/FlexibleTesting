using LegacyCodeProject.Core;

namespace LegacyCodeProject.Viewmodels;

public partial class UserViewModel<T> : UserViewModelBase
{
    public UserViewModel(SomeDataObject someDataObject)
        : base(someDataObject)
    {
        Token = Guid.NewGuid().ToString();
    }

    public string Token { get; set; }
    public DateTime CreatedAt { get; set; }

    protected void GenericMethod<TMethod>(TMethod value)
    {
        Token = value?.ToString() ?? "null";
    }
}
