# Build & Verify Workflow

Aways first read @file:README.md for the general idea of the codebase.

This project uses a custom code generation task. To develop and verify changes, follow this workflow:

1. **Modify Code**: Apply necessary changes to `FlexibleTesting.Tasks`.
2. **Build**: Execute `dotnet build FlexibleTesting.slnx`. This triggers the generator via MSBuild.
3. **Verify**:
    - **Check Logs**: Monitor build output for errors and warnings.
    - **Inspect Generated Files**: Examine `LegacyCodeProjectTests/Generated/*.g.cs` files to verify that the generated code correctly matches the intended design (e.g., correct constructor signatures, preserved attributes).

Repeat until generated code is correct and project builds without errors.
