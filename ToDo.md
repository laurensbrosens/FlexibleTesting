# To-do

This file records feature gaps and defects found while reviewing the documentation and current implementation. Items below are intentionally deferred unless explicitly marked as completed.

## Missing or incomplete functionality

- [ ] Implement `Overwrites.ReplaceProperty(...)`; it is declared in `FlexibleTestingDomain/Overwrites.cs` but is not handled by `FlexibleTestingInstructionsCreator`.
- [ ] Implement `Overwrites.Replace(...)`; it is declared but currently has no instruction-parser or rewriter support.
- [ ] Implement `Overwrites.MockSignature(...)`; the instruction model has signature-mocking collections, but the parser never records this operation.
- [ ] Implement `Overwrites.RedirectNew(...)` for redirecting object construction to a replacement delegate.
- [ ] Implement `Overwrites.MockWithInterface<TClass, TInterface>()`.
- [ ] Implement `Overwrites.InheritFrom<TClass>()`.
- [ ] Complete `Overwrites.Include<T>()`. It currently merges only selected mock collections and dependency names; publicizing members, signature mocks, inheritance flags, and sealed-removal settings are not merged.
- [ ] Add the documented operation for forcing boolean properties to a chosen value.
- [ ] Decide whether `GeneratorInstructionsAttribute.InterfacesTypes` is supported; it is exposed publicly but currently unused.
- [ ] Decide whether generated output should be split into separate generated classes/files as described in `implementation.md`.

## Deferred bugs and implementation risks

- [ ] Property mocks rewrite assignment targets as dependency writes. For example, `Now = DateTime.Now` becomes `_dependencies.Now = _dependencies.DateTime_Now`, leaving the generated `Now` property unchanged. Current status: 2 failing tests.
- [ ] `FlexibleTestingRewriter` renames and modifies every class and constructor in a source document instead of restricting changes to the selected target class.
- [ ] Base and mocked-class property stubs can emit empty accessor bodies (`get { }` / `set { }`), which are invalid for interface properties and non-void getters.
- [ ] Generated parameters do not preserve `ref`, `out`, `in`, or `params` modifiers.
- [ ] Default parameter values and attribute constructor arguments are emitted as string literals regardless of their original type.
- [ ] Dependency member naming is hardened for mocked members, but mocked-class interface names still use simple type names and can collide for same-named types in different namespaces.
- [ ] Generated files are included twice by the SDK default compile items and the explicit `Compile Include`, producing CS2002 warnings.
- [ ] The generator synchronously waits on Roslyn tasks in several places, producing VSTHRD002 warnings and creating a potential Visual Studio/MSBuild deadlock risk.
- [ ] The test project hardcodes `Debug` task assembly paths, so Release builds can load stale or missing task binaries.
- [ ] Generated output is written into the source tree without an incremental input/output contract; removed or renamed builders can leave stale generated files behind.
- [ ] Builders with missing/invalid targets are silently skipped instead of producing actionable diagnostics.
- [ ] `SymbolSignatureComparer` does not distinguish parameter ref-kinds, which can cause incorrect member matching for overloads involving `ref`, `in`, or `out`.
- [ ] Classes with no dependencies still receive an unused dependency field and empty dependency interface, as visible in `SealedClass_G`.

## Documentation alignment

- [ ] Reconcile `README.md`, `concept.md`, and `implementation.md`: they disagree about whether Include and recursive inheritance are implemented, and `implementation.md` claims replacement and naming fixes that are not fully reflected in behavior.
- [ ] Add examples documenting the collision-qualified dependency naming convention.

