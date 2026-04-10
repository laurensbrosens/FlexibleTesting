using LegacyTestability;
using Legacy.App;

namespace MyTests.Generation;

public sealed class MyLegacyRules : TestabilityInstructions
{
    public override void Configure()
    {
        For<CustomerViewModel>()
            .WithSuffix("_TestClass")
            .Publicize("OnLoadedAsync", "UserOnPropertyChanged", "UpdateDisplayName") // Publicize methods that need to be tested
            .RewriteMethod("OnLoadedAsync", "/* Body rewritten by source generator */") // Example rewrite, though we may need a more specific body
            .InjectMember(@"public string __TestHook() => ""ok"";");
    }
}
