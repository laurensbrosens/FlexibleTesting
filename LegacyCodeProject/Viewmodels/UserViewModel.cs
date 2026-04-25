using LegacyCodeProject.Core;

namespace LegacyCodeProject.Viewmodels;

public partial class UserViewModel<T> : UserViewModelBase
{
    public UserViewModel(SomeDataObject someDataObject)
        : base(someDataObject)
    {
        Token = Guid.NewGuid().ToString();
        Now = DateTime.Now;
        var test1 = Now;
        var test2 = DateTime.Now;
    }

    public string Token { get; set; }
    public DateTime Now {  get; set; }

    protected void GenericMethod<TMethod>(TMethod value)
    {
        Token = value?.ToString() ?? "null";
    }
}
