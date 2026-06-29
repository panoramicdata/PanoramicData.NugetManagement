# Bootstrap Guide: Ideal New NuGet Package for Panoramic Data

## Purpose

Use this document to bootstrap a new NuGet package repository that will pass the governance expectations enforced by this repo (`PanoramicData.NugetManagement`) and match the quality bar demonstrated by the Uk.Parliament ecosystem.

Paragon repository:
- https://github.com/panoramicdata/Uk.Parliament

Paragon package example:
- https://www.nuget.org/packages/Uk.Parliament

## What "Ideal" Means Here

An ideal package in this ecosystem has all of the following:

1. Clean, reproducible, automated publishing with Nerdbank.GitVersioning (NBGV).
2. Complete package metadata and symbols.
3. Strict build quality (`TreatWarningsAsErrors`, nullable, docs).
4. Central Package Management (CPM), no inline package versions in project files.
5. Strong documentation (`README`, `CONTRIBUTING`, `SECURITY`, license).
6. CI + CodeQL + dependency automation.
7. Tests in xUnit v3 with coverage tooling.
8. No secrets in source control.
9. The use of AwesomeAssertions in unit tests

## Required Repository Assets

Create these files at minimum.

### Root files

- `.editorconfig`
- `global.json`
- `version.json`
- `Directory.Build.props`
- `Directory.Packages.props`
- `README.md`
- `CONTRIBUTING.md`
- `SECURITY.md`
- `LICENSE` (MIT)
- `Publish.ps1`
- `nuget-key.example.txt` (if you support local/manual publish scripts)
- `.gitignore`

### GitHub automation

- `.github/workflows/ci.yml`
- `.github/workflows/codeql.yml`
- `.github/dependabot.yml`
- `.github/copilot-instructions.md` (optional but recommended)

### Project structure

- `Your.Package/Your.Package.csproj`
- `Your.Package.Test/Your.Package.Test.csproj`
- `Your.Package.slnx`

Optional but recommended:
- `PUBLISHING.md`
- `CHANGELOG.md`
- project icon (for `PackageIcon`)

## Required Content by File

## 1) `.editorconfig`

Must enforce:
- tabs (4) for most files
- markdown trailing whitespace rule relaxed
- C# analyzers and namespace style (file-scoped)
- naming conventions

Baseline pattern should mirror this repo. If in doubt, copy this repo's `.editorconfig` and adapt only when required.

## 2) `global.json`

Pin the SDK and roll-forward behavior:

```json
{
  "sdk": {
    "version": "10.0.300",
    "rollForward": "latestFeature"
  }
}
```

## 3) `version.json` (NBGV)

Use NBGV for deterministic versioning. Example:

```json
{
  "version": "1.0",
  "publicReleaseRefSpec": [
    "^refs/heads/main$",
    "^refs/tags/\\d+\\.\\d+\\.\\d+$"
  ]
}
```

Notes:
- Keep tag format consistent with your CI and publish script.
- Do not manually move existing tags.

## 4) `Directory.Build.props`

Must include at least:

```xml
<Project>

	<PropertyGroup>
		<Authors>Panoramic Data Limited</Authors>
		<Company>Panoramic Data Limited</Company>
		<Copyright>Copyright © $([System.DateTime]::Now.Year) Panoramic Data Limited</Copyright>
		<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
		<Nullable>enable</Nullable>
		<GenerateDocumentationFile>true</GenerateDocumentationFile>
		<NuGetAuditMode>All</NuGetAuditMode>
	</PropertyGroup>

</Project>
```

## 5) `Directory.Packages.props`

Enable CPM and keep versions centralized:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Project>
	<PropertyGroup>
		<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
	</PropertyGroup>
	<ItemGroup>
		<PackageVersion Include="Nerdbank.GitVersioning" Version="3.9.50" />
		<PackageVersion Include="xunit.v3" Version="3.2.2" />
		<PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
		<PackageVersion Include="coverlet.collector" Version="10.0.1" />
	</ItemGroup>
</Project>
```

Rules:
- No `Version=` in `PackageReference` inside `.csproj` files.
- Add all package versions here.

## 6) Package `.csproj`

Your main package project must contain complete NuGet metadata and packing behavior.

Minimum baseline:

```xml
<Project Sdk="Microsoft.NET.Sdk">

	<PropertyGroup>
		<TargetFramework>net10.0</TargetFramework>
		<ImplicitUsings>enable</ImplicitUsings>
		<NeutralResourcesLanguage>en</NeutralResourcesLanguage>

		<PackageId>Your.Package</PackageId>
		<Owners>Panoramic Data Limited</Owners>
		<PackageProjectUrl>https://github.com/panoramicdata/Your.Package</PackageProjectUrl>
		<RepositoryUrl>https://github.com/panoramicdata/Your.Package</RepositoryUrl>
		<RepositoryType>git</RepositoryType>
		<PackageLicenseExpression>MIT</PackageLicenseExpression>
		<PackageIcon>Logo.png</PackageIcon>
		<Description>Clear one-line package description.</Description>
		<PackageRequireLicenseAcceptance>false</PackageRequireLicenseAcceptance>
		<IncludeSymbols>true</IncludeSymbols>
		<SymbolPackageFormat>snupkg</SymbolPackageFormat>
		<GeneratePackageOnBuild>true</GeneratePackageOnBuild>
		<PackageTags>NuGet;PanoramicData</PackageTags>
		<PackageReadmeFile>README.md</PackageReadmeFile>
	</PropertyGroup>

	<ItemGroup>
		<PackageReference Include="Nerdbank.GitVersioning" PrivateAssets="all" />
	</ItemGroup>

	<ItemGroup>
		<None Include="Logo.png" Pack="true" PackagePath="" />
		<None Include="..\README.md" Link="README.md" Pack="true" PackagePath="" />
	</ItemGroup>

</Project>
```

## 7) `README.md`

Must include:

1. Badges: NuGet version, license, Codacy (if configured), target .NET.
2. Installation section (`dotnet add package ...`).
3. Quick start with real C# usage.
4. Feature/capability summary.
5. Test coverage status or quality statement.
6. Links: NuGet package, GitHub repo, issue tracker.
7. License statement.

Use Uk.Parliament as model quality for:
- clarity of quick start
- breadth of examples
- explicit capability status
- honest known issues

## 8) `CONTRIBUTING.md`

Must specify:
- branch/PR flow
- coding standards (XML docs, System.Text.Json preference, Refit where applicable)
- test expectations
- zero diagnostics expectation

## 9) `SECURITY.md`

Must specify:
- supported versions
- private disclosure process
- security contact email
- no public issue disclosure for vulnerabilities

## 10) `Publish.ps1`

Must enforce:
- clean git working tree
- on `main`
- local branch synced with remote
- version from NBGV
- no duplicate tag
- push tag to trigger CI publish

This repo's script is a good baseline.

## 11) `.github/workflows/ci.yml`

Must include:
- `actions/checkout@v4` with `fetch-depth: 0`
- setup .NET 10
- restore + build + pack
- upload `.nupkg` and `.snupkg` artifacts
- publish job triggered only for tags
- NuGet Trusted Publishing (`NuGet/login@v1`) before push

## 12) `.github/workflows/codeql.yml`

Must include:
- C# analysis matrix
- build in CI before analyze
- scheduled run (weekly)

## 13) `.github/dependabot.yml`

Must include updates for:
- `nuget`
- `github-actions`

Weekly schedule is recommended.

## 14) Test project

Use xUnit v3. Must include:
- `Microsoft.NET.Test.Sdk`
- `xunit.v3`
- `xunit.runner.visualstudio`
- `coverlet.collector`

Recommended conventions:
- keep tests deterministic
- split unit/integration clearly
- document any required secrets in `secrets.example.json`

### Unit tests must never be skipped

Unit tests must always run and must always pass or fail — never skip. A skipped test gives a false sense of security and hides real problems.

Every **unit test project** must include an `xunit.runner.json` file alongside its `.csproj`:

```json
{
	"$schema": "https://xunit.net/schema/current/xunit.runner.schema.json",
	"failSkips": true
}
```

This causes xUnit v3 to treat any skipped test as a failure, making skips immediately visible in CI.

**Integration test projects** are the only permitted exception. When a test legitimately skips due to a missing credential or unavailable external dependency, set `failSkips: false` explicitly and document the reason in the project README or a code comment. Prefer keeping integration tests in a **separate project** from unit tests so that `failSkips: true` can be enforced unconditionally in the unit test project.

## Quality Gates (Must Pass Before Release)

1. `dotnet restore`
2. `dotnet build --configuration Release`
3. `dotnet test --configuration Release`
	- Zero skipped tests in unit test projects (enforced by `failSkips: true` in `xunit.runner.json`)
4. `dotnet pack <main-csproj> --configuration Release`
5. verify generated `.nupkg` and `.snupkg`
6. verify README renders on NuGet package page
7. verify no secrets in diff/history

## Optional but Strongly Recommended Assets

- `PUBLISHING.md` with first-time setup and troubleshooting.
- `nuget-key.example.txt` showing expected local secret file format.
- release checklist in repo docs.
- explicit migration notes for breaking changes.

These are done well in the Uk.Parliament repository documentation set.

## Bootstrap Sequence (Practical Order)

1. Create solution + main/test projects.
2. Add root governance files (`global.json`, `version.json`, `Directory.*.props`, `.editorconfig`).
3. Add package metadata to main `.csproj`.
4. Add README + LICENSE + CONTRIBUTING + SECURITY.
5. Configure CI, CodeQL, Dependabot.
6. Add tests and get green build/test locally.
7. Validate package output and symbols.
8. Add/verify publish script and first tag flow on a test release.

## Anti-Patterns to Avoid

- Inline package versions in `.csproj` when CPM is enabled.
- Missing XML docs on public API.
- `GeneratePackageOnBuild=false` for a package project.
- Missing `PackageReadmeFile` or forgetting to pack linked README.
- Shallow checkout in CI (breaks NBGV correctness).
- Manual version editing in multiple places instead of NBGV.
- Publishing from dirty working tree.
- Committing API keys or tokens.

## Definition of Done for a New Package Repo

A new package repo is done when all are true:

1. First package version publishes from CI using tag-triggered flow.
2. NuGet page shows README, icon, license, symbols.
3. Build, test, and pack succeed with zero warnings.
4. Governance scanner rules in this repo report compliant status for the new package repo.
5. Documentation is sufficient for a new maintainer to release without tribal knowledge.

## Quick Compliance Checklist

- [ ] `TargetFramework` is `net10.0`
- [ ] `TreatWarningsAsErrors` is true
- [ ] `Nullable` is enabled
- [ ] XML docs generated
- [ ] NBGV configured with `version.json`
- [ ] CPM enabled and used correctly
- [ ] Package metadata complete (`PackageId`, `RepositoryUrl`, `PackageLicenseExpression`, `PackageIcon`, `PackageReadmeFile`)
- [ ] Symbols enabled (`snupkg`)
- [ ] README includes install + quick start + badges
- [ ] LICENSE/SECURITY/CONTRIBUTING present
- [ ] CI + CodeQL + Dependabot configured
- [ ] `Publish.ps1` validates clean git and pushes version tag

---

If you apply this file as-is when starting a new package, you will align closely with the repository governance rules and the quality posture exemplified by Uk.Parliament.