namespace Legacy.Tests.TestDoubles;

public static class TestEmail
{
    public static bool IsValidEmail(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains("@") && value.Contains(".");
}