namespace LegacyCodeProjectCore;

public class ViewModelCore(string testString)
{
    public DateTime CreatedAt { get; set; }
    public string TestString { get; set; } = testString;

    protected void SomeMethod()
    {
        CreatedAt = DateTime.Now;
    }

}
