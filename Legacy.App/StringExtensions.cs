namespace Legacy.App;

public static class StringExtensions
{
    public static bool IsValidEmail(this string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains("@");
}