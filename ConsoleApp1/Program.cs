using FlexibleTesting;

namespace ConsoleApp1;

public interface IUserInterface
{
    int InterfaceProperty { get; set; }
}

public interface IUserInterface2
{
    float InterfacePropertyOnlyGetter { get; }
}

[AutoImplementProperties(typeof(IUserInterface), typeof(IUserInterface2))]
public partial class UserClass
{
    public string UserProp { get; set; }
}

public class Test
{
    public void TestMethod()
    {
        UserClass user = new();
        user.UserProp = "Hello";
        user.InterfaceProperty = 1;
    }
}

