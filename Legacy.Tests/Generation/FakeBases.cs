namespace Legacy.Tests.Generation;

public static class FakeBases
{
    public static FakeBaseMapBuilder Map<TRealBase, TFakeBase>() => new();

    public sealed class FakeBaseMapBuilder
    {
        public FakeBaseMapBuilder PublicizeOverrides(bool enabled = true) => this;
    }
}