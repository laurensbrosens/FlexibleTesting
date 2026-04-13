
using LegacyCodeProject.Viewmodels;

namespace LegacyCodeProject.Core;

public class SomeDataObject
{
    public int MyProperty { get; set; }
    public int MyProperty1 { get; set; }
    public void Test()
    {
        // var test = new UserViewModel_G(this);
        var test2 = new UserViewModel(this);
    }
}
