Fix property mocking and interface naming collision.

**Goal:**
1. Ensure dependency interface members use deterministic names (e.g., `DateTime.Now` -> `DateTime_Now`).
2. Fix broken `Overwrites.Mock(() => Property)` behavior.

**Naming Convention:**
Always use fully qualified path or deterministic path for interface members, e.g., `DateTime.Now` becomes `DateTime_Now` on `IAutoDependencies`.

**Testing Strategy:**
1. Modify `@LegacyCodeProject/Viewmodels/UserViewModel.cs` to add properties that test edge cases (e.g., both static `DateTime.Now` and instance `Now`).
2. Run `dotnet build FlexibleTesting.slnx` to trigger generator.
3. Inspect `@LegacyCodeProjectTests/Generated/UserViewModel_G.g.cs` to verify deterministic names.

Refactor `FlexibleTestingInstructionsCreator`, `FlexibleTestingCodeGenerator`, and `FlexibleTestingTask` as needed.