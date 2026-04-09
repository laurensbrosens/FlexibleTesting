using System.Collections.Generic;

namespace Legacy.Tests.TestDoubles;

public static class TestFile
{
    private static readonly Dictionary<string, string> Data = new();

    public static void Clear() => Data.Clear();

    public static void SetText(string path, string text) => Data[path] = text;

    public static string ReadAllText(string path) => Data.TryGetValue(path, out var v) ? v : "";
}