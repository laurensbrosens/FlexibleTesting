using System;

namespace Legacy.Tests.TestDoubles;

public static class TestClock
{
    public static DateTime Now { get; set; } = new DateTime(2000, 1, 1);
}