# Contributing to PanoramicData.NugetManagement

We welcome contributions! Please follow these guidelines to keep things smooth.

## Getting Started

1. Fork the repository
2. Create a feature branch from `main`
3. Make your changes
4. Ensure all tests pass (`dotnet test`)
5. Open a pull request

## Development Requirements

- .NET 10.0 SDK
- An IDE that supports .editorconfig (e.g. Visual Studio, Rider, VS Code with C# Dev Kit)

## Code Standards

- Follow the `.editorconfig` rules
- Use file-scoped namespaces
- Add XML documentation comments to all public members
- Use `required` keyword for required DTO properties
- Prefer `System.Text.Json` over `Newtonsoft.Json`
- Use Refit for any HTTP client interfaces
- Keep `TreatWarningsAsErrors` enabled — fix warnings, don't suppress them

## Adding New Rules

1. Create a new class extending `RuleBase` in the `Rules/` directory
2. Group related rules in the same file by category
3. Assign a unique `RuleId` following the pattern: `{CATEGORY}-{NN}` (e.g. `CI-08`)
4. Add the appropriate `AssessmentCategory` and `AssessmentSeverity`
5. Provide clear `Message` and `Remediation` text in `Fail()` results
6. The rule will be automatically discovered by `RuleRegistry`

## Pull Request Checklist

- [ ] Code compiles with zero warnings
- [ ] All existing tests pass
- [ ] New rules have corresponding tests
- [ ] XML documentation on public API
- [ ] No Newtonsoft.Json references
- [ ] No suppressed warnings without justification

## License

By contributing, you agree that your contributions will be licensed under the MIT License.
