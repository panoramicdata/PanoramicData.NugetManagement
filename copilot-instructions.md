# Copilot Instructions

## Project Overview

**PanoramicData.NugetManagement** is an opinionated governance tool that connects to a GitHub organization, examines each repository, and evaluates it against a comprehensive set of best-practice rules for NuGet package publishing. It also provides a local Blazor Server dashboard for reviewing results and applying automated remediations.

## Solution Structure

| Project | Purpose |
|---------|---------|
| `PanoramicData.NugetManagement` | Core library — rule engine, models, GitHub/NuGet services. Published as a NuGet package. |
| `PanoramicData.NugetManagement.Web` | Blazor Server dashboard for governance review and one-click remediation. Not packable. |
| `PanoramicData.NugetManagement.Test` | xUnit v3 tests (unit + integration). |

## Target Framework

All projects target **.NET 10** (`net10.0`).

## Key Architecture Patterns

### Rules Engine
- Rules implement `IRule` (in `Rules/`), grouped into category subfolders (e.g. `BuildQuality/`, `CiWorkflow/`, `Testing/`).
- Rules are auto-discovered via reflection in `RuleRegistry`.
- Each rule has a `RuleId` (e.g. `"BLD-01"`), `Category` (`AssessmentCategory` enum), and `Severity` (`AssessmentSeverity` enum: Info, Warning, Error).
- `OrganizationAssessor` orchestrates assessment across all repos concurrently.

### Remediations (Web project)
- Remediations implement `IRemediation` (in `Remediations/`), mirroring the rule category folder structure.
- `RemediationRegistry` discovers them. Each remediation is tied to a `RuleId` and applies filesystem fixes to a local clone.
- `DataDrivenRemediation` provides a generic data-driven approach for simple property-based fixes.

### Services
- `RepositoryContextBuilder` / `LocalRepositoryContextBuilder` — builds a `RepositoryContext` from GitHub API or local filesystem.
- `NuGetVersionChecker` — queries NuGet for latest package versions.
- `DashboardService`, `DashboardCacheService`, `LocalRepoService` — web dashboard services.

## Build & Package Management

- **Central Package Management (CPM)**: all package versions are in `Directory.Packages.props`; `.csproj` files use `<PackageReference>` without `Version`.
- **Directory.Build.props** sets: `TreatWarningsAsErrors`, `Nullable enable`, `GenerateDocumentationFile`, `NuGetAuditMode All`.
- **Versioning**: Nerdbank.GitVersioning via `version.json`.

## Code Style & Conventions

- **Indentation**: tabs (4-wide) — enforced by `.editorconfig`.
- **Namespaces**: file-scoped (`namespace X;`).
- **Nullable**: enabled globally.
- **Implicit usings**: enabled.
- **XML doc comments**: required (GenerateDocumentationFile is on; TreatWarningsAsErrors will fail missing docs).

## Key Dependencies

| Package | Used For |
|---------|----------|
| `Octokit` | GitHub API access |
| `NuGet.Protocol` | Querying NuGet feeds |
| `Refit` | Typed HTTP client generation |
| `Codacy.Api` | Codacy quality gate integration |
| `PanoramicData.Blazor` | Blazor UI component library |
| `AspNet.Security.OAuth.GitHub` | GitHub OAuth in the web app |

## Testing

- **Framework**: xUnit v3 (`xunit.v3`), with `Xunit.Microsoft.DependencyInjection` for DI in tests.
- **Assertions**: AwesomeAssertions (fork of FluentAssertions).
- **Coverage**: coverlet.collector.
- **Test fixtures**: `PanoramicData.NugetManagement.Test/Fixtures/PanoramicData.NugetFailArmy/` contains a deliberately non-compliant repo fixture for rule validation.

## Build Guidance

- When building to verify changes, build only the specific affected project rather than the full solution. Use targeted build commands like `dotnet build PanoramicData.NugetManagement.Web/PanoramicData.NugetManagement.Web.csproj`.

## Web App Notes

- The web project is **Blazor Server** (interactive server-side rendering).
- Authentication: GitHub OAuth (optional; falls back to cookie-only when client ID/secret are not configured).
- Configuration: `AppSettings` section bound from `appsettings.json` / user secrets.
