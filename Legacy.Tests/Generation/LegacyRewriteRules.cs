using System;
using System.IO;
using Legacy.App;
using Legacy.Tests.Fakes;
using Legacy.Tests.TestDoubles;

namespace Legacy.Tests.Generation;

public static class LegacyRewriteRules
{
    public static void Configure()
    {
        Overwrites.Replace<Func<DateTime>>(
            () => DateTime.Now,
            () => TestClock.Now);

        Overwrites.Replace<Func<string, string>>(
            path => File.ReadAllText(path),
            path => TestFile.ReadAllText(path));

        Overwrites.Replace<Func<string, bool>>(
            s => s.IsValidEmail(),
            s => TestEmail.IsValidEmail(s));

        FakeBases.Map<HeavyBaseViewModel, HeavyBaseViewModel_Fake>().PublicizeOverrides();
    }
}