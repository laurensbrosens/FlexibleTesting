# Refactoring Progress

- [x] 1. Remove magic strings everywhere (typeof/nameof/Roslyn types).
- [x] 2. Always use `{}` with every if/else/elseif.
- [x] 3. Use switches for CodeAnalysis Syntaxes with comments on unimplemented cases.
- [ ] 4. Refactor Mockable records into a single class with ISymbol and signature-based comparison.
- [ ] 5. Split FlexibleInstruction creation and code generation into new classes.
- [x] 6. Clean and SOLID refactor, improve naming verbosity.

---
# Current Task
Starting Point 4: Refactoring Mockable records and FlexibleTestingInstructions into a single class.
Moving towards a single class for instructions/mockables using ISymbols and custom comparison.
