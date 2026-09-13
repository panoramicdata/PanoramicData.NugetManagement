# Coverage to Codacy on the self-hosted runner Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Collect line coverage in CI on the organisation's own runner, upload it to Codacy, read it back for every repository in the estate, and grade each one RED/AMBER/BLUE/GREEN.

**Architecture:** `Run-Coverage.ps1` becomes cross-platform and runs on the `pdl-prod-cluster` ARC scale set, uploading Cobertura to Codacy. A new `CodacyCoverageService` reads the resulting percentage back through the existing `Codacy.Api` typed client. `RepositoryContextBuilder` uses it to populate `RepositoryContext.LineCoveragePercent`, which has never been assigned in production code. A new rule, TST-10, bands that figure.

**Tech Stack:** .NET 10, xUnit v3 on Microsoft.Testing.Platform, AwesomeAssertions, `Codacy.Api` 3.0.45 (Refit-based), GitHub Actions on Actions Runner Controller, PowerShell 7.

**Spec:** [docs/superpowers/specs/2026-09-13-coverage-codacy-self-hosted-design.md](../specs/2026-09-13-coverage-codacy-self-hosted-design.md)

## Global Constraints

- Target framework is `net10.0`. Do not add packages; everything needed is already referenced.
- Tabs for indentation, file-scoped namespaces. The repository's own rules (CQ-02, CQ-04) enforce both.
- Tests use xUnit v3 with AwesomeAssertions (`using AwesomeAssertions;`, `result.Should().Be(...)`). Never `Assert.*`.
- Rules are discovered by reflection in `RuleRegistry.DiscoverRules` via `Activator.CreateInstance`. **Every rule must have a public parameterless constructor** or the whole registry throws at startup.
- The coverage bands, exactly: RED = no figure or 0%; AMBER = >0 and <30; BLUE = >=30 and <60; GREEN = >=60.
- The band severities, exactly: RED = `AssessmentSeverity.Error`; AMBER = `AssessmentSeverity.Warning`; BLUE = `AssessmentSeverity.Info`; GREEN = a pass.
- The self-hosted runner label is `pdl-prod-cluster`. Use list form: `runs-on: [pdl-prod-cluster]`.
- Only a Codacy 404 means "no answer". Every other exception must propagate. Use the existing `CodacyNotFound.Matches(ex)` helper; never catch `Exception` bare.
- Do not change Codacy's `minCoveragePercentage`. GREEN was set to 60 to match what Codacy already applies.
- Run the test suite with `dotnet test`. Exit code 5 means an MTP configuration problem, 1 usually means the dev server holds a file lock, 2 means real failures.
- Stop any running dev server before building, or you compile nothing.

---

### Task 1: Make `Run-Coverage.ps1` cross-platform

The script hardcodes a `.exe` suffix. On the Linux ARC runner the Microsoft.Testing.Platform executable has no extension, so the script cannot run there at all. This is a prerequisite for every later CI task.

**Files:**
- Modify: `Run-Coverage.ps1:36`
- Test: none (a shell script with no test harness in this repository; verified by running it)

**Interfaces:**
- Consumes: nothing.
- Produces: `Run-Coverage.ps1` runs on Linux and Windows, writing `PanoramicData.NugetManagement.Test/bin/Debug/net10.0/TestResults/coverage.cobertura.xml`.

- [ ] **Step 1: Replace the hardcoded executable name**

Find this line:

```powershell
$testExecutable = Join-Path $testProject 'bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe'
```

Replace it with:

```powershell
# The Microsoft.Testing.Platform host is a native executable, so its name is platform-dependent:
# ".exe" on Windows, no extension anywhere else. $IsWindows is an automatic variable in PowerShell 7.
$executableName = if ($IsWindows) { 'PanoramicData.NugetManagement.Test.exe' } else { 'PanoramicData.NugetManagement.Test' }
$testExecutable = Join-Path $testProject "bin/Debug/net10.0/$executableName"
```

- [ ] **Step 2: Run it on this machine to confirm Windows still works**

Run: `pwsh ./Run-Coverage.ps1`

Expected: the script prints "Line coverage:" and "Branch coverage:" followed by per-module percentages, and `PanoramicData.NugetManagement.Test/bin/Debug/net10.0/TestResults/coverage.cobertura.xml` exists afterwards.

If it fails with a file lock, stop the running dev server and try again.

- [ ] **Step 3: Record the measured figure**

Write down the line coverage percentage the script printed. Task 7 needs it to sanity-check the band the rule reports.

- [ ] **Step 4: Commit**

```bash
git add Run-Coverage.ps1
git commit -m "fix: run the coverage script on Linux as well as Windows"
```

---

### Task 2: Move CI to the self-hosted runner and add the coverage job

**Files:**
- Modify: `.github/workflows/ci.yml`
- Test: none (verified by the workflow run itself)

**Interfaces:**
- Consumes: `Run-Coverage.ps1` from Task 1.
- Produces: a `coverage` job that uploads `coverage.cobertura.xml` to Codacy using the `CODACY_PROJECT_TOKEN` repository secret, which is already set.

- [ ] **Step 1: Add the shared environment block**

Immediately after the `on:` block and before `jobs:`, add:

```yaml
# FORCE_JAVASCRIPT_ACTIONS_TO_NODE24 is not optional on this runner: the actions-runner image the
# pdl-prod-cluster scale set uses does not offer the Node version several of these actions request
# by default, and without it they fail before their first step runs.
env:
  DOTNET_NOLOGO: 1
  DOTNET_CLI_TELEMETRY_OPTOUT: 1
  NUGET_XMLDOC_MODE: skip
  FORCE_JAVASCRIPT_ACTIONS_TO_NODE24: true
```

- [ ] **Step 2: Point both existing jobs at the self-hosted runner**

There are two occurrences of `runs-on: ubuntu-latest` (the `build` job and the `publish` job). Replace both with:

```yaml
    runs-on: [pdl-prod-cluster]
```

- [ ] **Step 3: Add the coverage job**

Add this as a new top-level job under `jobs:`, after `build` and before `publish`:

```yaml
  coverage:
    runs-on: [pdl-prod-cluster]

    steps:
    - uses: actions/checkout@v7
      with:
        fetch-depth: 0

    - name: Setup .NET
      uses: actions/setup-dotnet@v6
      with:
        dotnet-version: '10.0.x'

    # The actions-runner image ships no pwsh, and installing it through the SDK needs neither root
    # nor apt. Run-Coverage.ps1 is deliberately the single invocation used both here and locally.
    - name: Install PowerShell
      run: dotnet tool install --global PowerShell

    - name: Run tests with coverage
      run: pwsh ./Run-Coverage.ps1

    # continue-on-error: a Codacy outage must not turn a passing test run red. The trade-off is that
    # an expired project token fails silently — see the spec's note on the 2027-09-13 expiry.
    - name: Upload coverage to Codacy
      continue-on-error: true
      uses: codacy/codacy-coverage-reporter-action@v1
      with:
        project-token: ${{ secrets.CODACY_PROJECT_TOKEN }}
        coverage-reports: PanoramicData.NugetManagement.Test/bin/Debug/net10.0/TestResults/coverage.cobertura.xml

    - name: Upload coverage artifact
      uses: actions/upload-artifact@v7
      with:
        name: coverage
        path: PanoramicData.NugetManagement.Test/bin/Debug/net10.0/TestResults/coverage.cobertura.xml
        retention-days: 7
```

- [ ] **Step 4: Verify the YAML parses**

Run: `pwsh -Command "gh workflow view CI --repo panoramicdata/PanoramicData.NugetManagement" ; Get-Content .github/workflows/ci.yml | Select-String 'runs-on'`

Expected: three `runs-on: [pdl-prod-cluster]` lines and no remaining `ubuntu-latest`.

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: run on pdl-prod-cluster and upload coverage to Codacy"
```

---

### Task 3: `CodacyCoverageService`

Reads the coverage percentage Codacy holds for a repository. Modelled directly on `CodacyFileGradeService` — same interface-plus-sealed-class shape, same `CodacyNotFound` handling, same `using var client` lifetime.

**Files:**
- Create: `PanoramicData.NugetManagement/Services/CodacyCoverageService.cs`
- Test: `PanoramicData.NugetManagement.Test/CodacyCoverageServiceTests.cs`

**Interfaces:**
- Consumes: `Codacy.Api` — `client.Analysis.GetRepositoryWithAnalysisAsync(Provider.Github, org, repo, branch, ct)` returns a `RepositoryWithAnalysisResponse` whose `.Data.Coverage.CoveragePercentageWithDecimals` is the figure. `.Coverage` is null when Codacy reports coverage status "None".
- Produces:
  - `interface ICodacyCoverageService` with
    `Task<double?> GetLineCoveragePercentAsync(string apiToken, string organizationName, string repositoryName, string? branch, CancellationToken cancellationToken)`
  - `sealed class CodacyCoverageService : ICodacyCoverageService`

Task 4 consumes `ICodacyCoverageService` by that exact name and signature.

- [ ] **Step 1: Write the failing tests**

Create `PanoramicData.NugetManagement.Test/CodacyCoverageServiceTests.cs`. The real service constructs its own `CodacyClient`, so these tests exercise the decision logic through an injectable reader rather than over HTTP — the same split `CodacyAnalysisStateLookup` uses so its failure handling can be tested without a Codacy account.

```csharp
using AwesomeAssertions;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

public class CodacyCoverageServiceTests
{
	[Fact]
	public async Task ReadCoverageAsync_WhenCodacyReportsAPercentage_ReturnsIt()
	{
		var result = await CodacyCoverageService.ReadCoverageAsync(
			_ => Task.FromResult<double?>(42.5),
			CancellationToken.None);

		result.Should().Be(42.5);
	}

	[Fact]
	public async Task ReadCoverageAsync_WhenCodacyHasNoCoverage_ReturnsNull()
	{
		var result = await CodacyCoverageService.ReadCoverageAsync(
			_ => Task.FromResult<double?>(null),
			CancellationToken.None);

		result.Should().BeNull();
	}

	[Fact]
	public async Task ReadCoverageAsync_WhenRepositoryIsNotOnCodacy_ReturnsNull()
	{
		// CodacyNotFound.Matches recognises a 404 in any of the shapes the Refit-generated client can
		// throw it in. HttpRequestException is the one that is trivially constructible in a test.
		var notFound = new HttpRequestException("not found", null, System.Net.HttpStatusCode.NotFound);

		var result = await CodacyCoverageService.ReadCoverageAsync(
			_ => Task.FromException<double?>(notFound),
			CancellationToken.None);

		result.Should().BeNull();
	}

	[Fact]
	public async Task ReadCoverageAsync_WhenCodacyIsUnreachable_Propagates()
	{
		// The whole point of the 404-only rule. An unreachable Codacy reported as "no coverage" would
		// grade every repository RED on a network blip, and the AI fixer acts on RED.
		var unreachable = new HttpRequestException("connection refused");

		var act = async () => await CodacyCoverageService.ReadCoverageAsync(
			_ => Task.FromException<double?>(unreachable),
			CancellationToken.None);

		await act.Should().ThrowAsync<HttpRequestException>();
	}
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CodacyCoverageServiceTests"`

Expected: compilation failure — `CodacyCoverageService` does not exist.

- [ ] **Step 3: Write the service**

Create `PanoramicData.NugetManagement/Services/CodacyCoverageService.cs`:

```csharp
using Codacy.Api;
using Codacy.Api.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Reads the line coverage percentage Codacy holds for a repository.
/// </summary>
public interface ICodacyCoverageService
{
	/// <summary>
	/// The line coverage Codacy holds for a repository branch.
	/// </summary>
	/// <param name="apiToken">The Codacy API token.</param>
	/// <param name="organizationName">The GitHub organization name.</param>
	/// <param name="repositoryName">The repository name.</param>
	/// <param name="branch">The branch to query (typically the default branch); may be null.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>
	/// The percentage, or null when Codacy holds no coverage for the repository — either because it
	/// was never added, or because nothing has ever been uploaded.
	/// </returns>
	Task<double?> GetLineCoveragePercentAsync(
		string apiToken,
		string organizationName,
		string repositoryName,
		string? branch,
		CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="ICodacyCoverageService"/> backed by the Codacy.Api client.
/// </summary>
public sealed class CodacyCoverageService : ICodacyCoverageService
{
	/// <inheritdoc />
	public async Task<double?> GetLineCoveragePercentAsync(
		string apiToken,
		string organizationName,
		string repositoryName,
		string? branch,
		CancellationToken cancellationToken)
	{
		using var client = new CodacyClient(new CodacyClientOptions { ApiToken = apiToken });

		return await ReadCoverageAsync(
			async token => (await client.Analysis
				.GetRepositoryWithAnalysisAsync(Provider.Github, organizationName, repositoryName, branch, token)
				.ConfigureAwait(false)).Data.Coverage?.CoveragePercentageWithDecimals,
			cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// The same read over a supplied reader, so the failure handling can be tested without a Codacy
	/// account.
	/// </summary>
	/// <remarks>
	/// Only a 404 becomes null. Anything else propagates: an unreachable Codacy reported as "no
	/// coverage" would grade every repository RED on a network blip, and RED is what the AI fixer
	/// acts on. This is the same distinction <see cref="CodacyRepositoryLookup"/> exists to preserve.
	/// </remarks>
	internal static async Task<double?> ReadCoverageAsync(
		Func<CancellationToken, Task<double?>> readAsync,
		CancellationToken cancellationToken)
	{
		try
		{
			return await readAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (CodacyNotFound.Matches(ex))
		{
			return null;
		}
	}
}
```

- [ ] **Step 4: Make `ReadCoverageAsync` visible to the test project**

`ReadCoverageAsync` is `internal`. Check whether `PanoramicData.NugetManagement.csproj` already has an `InternalsVisibleTo` for the test project:

Run: `pwsh -Command "Select-String -Path PanoramicData.NugetManagement/PanoramicData.NugetManagement.csproj -Pattern 'InternalsVisibleTo'"`

If there is no match, add this inside the existing `<ItemGroup>` that holds the `PackageReference` entries:

```xml
		<InternalsVisibleTo Include="PanoramicData.NugetManagement.Test" />
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CodacyCoverageServiceTests"`

Expected: 4 passed.

If `.Data.Coverage?.CoveragePercentageWithDecimals` does not compile because `CoveragePercentageWithDecimals` is non-nullable `double`, change the lambda body to `?.CoveragePercentageWithDecimals is { } pct ? pct : (double?)null`. Do not change the interface.

- [ ] **Step 6: Commit**

```bash
git add PanoramicData.NugetManagement/Services/CodacyCoverageService.cs PanoramicData.NugetManagement.Test/CodacyCoverageServiceTests.cs PanoramicData.NugetManagement/PanoramicData.NugetManagement.csproj
git commit -m "feat: read line coverage back from Codacy"
```

---

### Task 4: Populate `RepositoryContext.LineCoveragePercent`

`LineCoveragePercent` is `init`-only and has never been assigned outside tests, so it must be set where the context is constructed. `OrganizationAssessor` resolves the Codacy token at line 143 and calls `BuildAsync` at line 147, so by the time the builder runs, `options.Codacy.ApiToken` is populated.

**Files:**
- Modify: `PanoramicData.NugetManagement/Services/RepositoryContextBuilder.cs:58-60` (constructors), `:120-131` (the `new RepositoryContext` block)
- Test: `PanoramicData.NugetManagement.Test/RepositoryContextCoverageTests.cs`

**Interfaces:**
- Consumes: `ICodacyCoverageService.GetLineCoveragePercentAsync(apiToken, organizationName, repositoryName, branch, cancellationToken)` from Task 3.
- Produces: `RepositoryContext.LineCoveragePercent` is populated for every repository with a Codacy token configured. Task 5's rule reads it.

- [ ] **Step 1: Write the failing test**

Create `PanoramicData.NugetManagement.Test/RepositoryContextCoverageTests.cs`:

```csharp
using AwesomeAssertions;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

public class RepositoryContextCoverageTests
{
	private sealed class StubCoverageService(double? percent) : ICodacyCoverageService
	{
		public string? RequestedRepository { get; private set; }

		public Task<double?> GetLineCoveragePercentAsync(
			string apiToken,
			string organizationName,
			string repositoryName,
			string? branch,
			CancellationToken cancellationToken)
		{
			RequestedRepository = repositoryName;
			return Task.FromResult(percent);
		}
	}

	[Fact]
	public async Task ResolveCoverageAsync_WithNoToken_DoesNotCallCodacy()
	{
		var stub = new StubCoverageService(80);

		var result = await RepositoryContextBuilder.ResolveCoverageAsync(
			stub,
			apiToken: null,
			organizationName: "panoramicdata",
			repositoryName: "Widget",
			branch: "main",
			CancellationToken.None);

		result.Should().BeNull();
		stub.RequestedRepository.Should().BeNull();
	}

	[Fact]
	public async Task ResolveCoverageAsync_WithToken_ReturnsTheCoverage()
	{
		var stub = new StubCoverageService(37.25);

		var result = await RepositoryContextBuilder.ResolveCoverageAsync(
			stub,
			apiToken: "a-token",
			organizationName: "panoramicdata",
			repositoryName: "Widget",
			branch: "main",
			CancellationToken.None);

		result.Should().Be(37.25);
		stub.RequestedRepository.Should().Be("Widget");
	}

	[Fact]
	public async Task ResolveCoverageAsync_WhenCodacyFails_ReturnsNullRatherThanFailingTheAssessment()
	{
		// A repository whose coverage could not be read is graded RED by TST-10, which is honest:
		// nothing was demonstrated. Aborting the whole assessment instead would lose every other
		// finding for that repository.
		var throwing = new ThrowingCoverageService();

		var result = await RepositoryContextBuilder.ResolveCoverageAsync(
			throwing,
			apiToken: "a-token",
			organizationName: "panoramicdata",
			repositoryName: "Widget",
			branch: "main",
			CancellationToken.None);

		result.Should().BeNull();
	}

	private sealed class ThrowingCoverageService : ICodacyCoverageService
	{
		public Task<double?> GetLineCoveragePercentAsync(
			string apiToken,
			string organizationName,
			string repositoryName,
			string? branch,
			CancellationToken cancellationToken)
			=> Task.FromException<double?>(new HttpRequestException("Codacy is down"));
	}
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~RepositoryContextCoverageTests"`

Expected: compilation failure — `RepositoryContextBuilder.ResolveCoverageAsync` does not exist.

- [ ] **Step 3: Add the resolver and the field**

In `RepositoryContextBuilder.cs`, add a field next to the existing private fields:

```csharp
	private readonly ICodacyCoverageService _coverage = new CodacyCoverageService();
```

Then add this method to the class:

```csharp
	/// <summary>
	/// The repository's line coverage according to Codacy, or null when it cannot be established.
	/// </summary>
	/// <remarks>
	/// Swallowing the failure here is deliberate and is the one place it is correct to do so. The
	/// service itself distinguishes a 404 from an outage precisely so that distinction survives; but
	/// an assessment that aborted because Codacy was unreachable would lose every other finding for
	/// that repository. An unread figure becomes RED, which claims only that nothing was shown.
	/// </remarks>
	internal static async Task<double?> ResolveCoverageAsync(
		ICodacyCoverageService coverage,
		string? apiToken,
		string organizationName,
		string repositoryName,
		string? branch,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(apiToken))
		{
			return null;
		}

		try
		{
			return await coverage
				.GetLineCoveragePercentAsync(apiToken, organizationName, repositoryName, branch, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return null;
		}
	}
```

- [ ] **Step 4: Call it from `BuildAsync`**

Immediately before the `return new RepositoryContext` statement, add:

```csharp
		var lineCoveragePercent = await ResolveCoverageAsync(
			_coverage,
			options.Codacy?.ApiToken,
			owner,
			repoName,
			defaultBranch,
			cancellationToken).ConfigureAwait(false);
```

Then add this property to the object initialiser, after `RepositoryConfig`:

```csharp
			LineCoveragePercent = lineCoveragePercent
```

Remember to add a comma after `RepositoryConfig = repositoryConfig`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~RepositoryContextCoverageTests"`

Expected: 3 passed.

- [ ] **Step 6: Commit**

```bash
git add PanoramicData.NugetManagement/Services/RepositoryContextBuilder.cs PanoramicData.NugetManagement.Test/RepositoryContextCoverageTests.cs
git commit -m "feat: populate LineCoveragePercent from Codacy"
```

---

### Task 5: `CodeCoverageBandRule` (TST-10)

**Files:**
- Create: `PanoramicData.NugetManagement/Rules/Testing/CodeCoverageBandRule.cs`
- Test: `PanoramicData.NugetManagement.Test/CodeCoverageBandRuleTests.cs`

**Interfaces:**
- Consumes: `RepositoryContext.LineCoveragePercent` (populated in Task 4) and `RepositoryContext.FindTestProjectFiles()`.
- Produces: a rule with `RuleId` `"TST-10"`, discovered automatically by `RuleRegistry`.

Note on the existing rule ID space: TST-08 is `AwesomeAssertionsRule` and TST-09 is `UserSecretsIdRule`. TST-10 is the next free ID. Do not reuse TST-08.

- [ ] **Step 1: Write the failing tests**

Create `PanoramicData.NugetManagement.Test/CodeCoverageBandRuleTests.cs`. Copy the context-construction helper from `CoverageBaselineTests.cs` if its shape differs from the one below — `RepositoryContext` has required `init` properties and this must match the real type.

```csharp
using AwesomeAssertions;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Test;

public class CodeCoverageBandRuleTests
{
	// Mirrors the Context helper in CoverageBaselineTests, which is the proven shape for this type.
	// FileContents is a Dictionary, so it needs `new() { ... }` — a collection expression does not
	// compile here.
	private static RepositoryContext ContextWith(double? coverage, bool hasTestProject = true)
		=> new()
		{
			FullName = "test-org/Acme.Widget",
			Name = "Acme.Widget",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = hasTestProject
				? ["Acme.Widget/Acme.Widget.csproj", "Acme.Widget.Test/Acme.Widget.Test.csproj"]
				: ["Acme.Widget/Acme.Widget.csproj"],
			FileContents = hasTestProject
				? new() { ["Acme.Widget.Test/Acme.Widget.Test.csproj"] = "<Project/>" }
				: new() { ["Acme.Widget/Acme.Widget.csproj"] = "<Project/>" },
			LineCoveragePercent = coverage
		};

	private static async Task<RuleResult> EvaluateAsync(double? coverage, bool hasTestProject = true)
		=> await new CodeCoverageBandRule()
			.EvaluateAsync(ContextWith(coverage, hasTestProject), CancellationToken.None);

	[Fact]
	public async Task NoTestProjects_IsNotApplicable()
	{
		var result = await EvaluateAsync(coverage: 90, hasTestProject: false);

		result.IsApplicable.Should().BeFalse();
		result.Passed.Should().BeTrue();
	}

	[Theory]
	[InlineData(null)]
	[InlineData(0d)]
	public async Task NoCoverage_IsRed(double? coverage)
	{
		var result = await EvaluateAsync(coverage);

		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Error);
		result.Advisory.Should().NotBeNull();
	}

	[Theory]
	[InlineData(0.1)]
	[InlineData(29.9)]
	public async Task BelowThirty_IsAmber(double coverage)
	{
		var result = await EvaluateAsync(coverage);

		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Warning);
		result.Advisory.Should().NotBeNull();
	}

	[Theory]
	[InlineData(30d)]
	[InlineData(59.9)]
	public async Task BetweenThirtyAndSixty_IsBlue(double coverage)
	{
		var result = await EvaluateAsync(coverage);

		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Info);
		result.Advisory.Should().NotBeNull();
	}

	[Theory]
	[InlineData(60d)]
	[InlineData(100d)]
	public async Task AtLeastSixty_IsGreen(double coverage)
	{
		var result = await EvaluateAsync(coverage);

		result.Passed.Should().BeTrue();
		result.IsApplicable.Should().BeTrue();
	}

	[Fact]
	public async Task TheAdvisoryCarriesTheMeasuredFigureAndBand()
	{
		var result = await EvaluateAsync(12.5);

		result.Advisory!.Data["measured_line"].Should().Be(12.5);
		result.Advisory.Data["band"].Should().Be("AMBER");
	}

	[Fact]
	public void RuleIdIsTst10()
		=> new CodeCoverageBandRule().RuleId.Should().Be("TST-10");
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CodeCoverageBandRuleTests"`

Expected: compilation failure — `CodeCoverageBandRule` does not exist.

- [ ] **Step 3: Write the rule**

Create `PanoramicData.NugetManagement/Rules/Testing/CodeCoverageBandRule.cs`:

```csharp
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Grades a repository's line coverage on the estate's four-band scale.
/// </summary>
/// <remarks>
/// The bands are the colours the dashboard already draws: <see cref="AssessmentSeverity.Error"/> is
/// red, <see cref="AssessmentSeverity.Warning"/> amber and <see cref="AssessmentSeverity.Info"/>
/// blue. GREEN sits at 60% to agree with the coverage gate Codacy already applies across this
/// organisation, so the dashboard and Codacy cannot disagree about whether a repository is good
/// enough.
///
/// Distinct from TST-07, which watches the direction of travel against each repository's own best
/// figure. This one states where the repository stands on one scale shared by the whole estate.
/// </remarks>
public class CodeCoverageBandRule : RuleBase
{
	/// <summary>Coverage at or above this is GREEN. Matches Codacy's minCoveragePercentage.</summary>
	private const double GreenThreshold = 60;

	/// <summary>Coverage below this, but above zero, is AMBER.</summary>
	private const double AmberCeiling = 30;

	/// <inheritdoc />
	public override string RuleId => "TST-10";

	/// <inheritdoc />
	public override string RuleName => "Code coverage meets the estate band";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Testing;

	/// <summary>
	/// The worst case this rule can report. The emitted result carries the band's own severity, which
	/// varies; this is what rule listings and filters sort on.
	/// </summary>
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (!context.FindTestProjectFiles().Any())
		{
			// Not RED. A repository with no tests is TST-01's finding, and reporting it twice makes
			// the fix list longer without making it more informative.
			return Task.FromResult(NotApplicable("No test projects found; there is nothing to measure."));
		}

		var line = context.LineCoveragePercent;

		if (line >= GreenThreshold)
		{
			return Task.FromResult(Pass($"Line coverage is {line:N1}% — GREEN."));
		}

		var (band, severity, message) = line switch
		{
			null => ("RED", AssessmentSeverity.Error,
				"No coverage figure is available from Codacy."),
			<= 0 => ("RED", AssessmentSeverity.Error,
				"Line coverage is 0%."),
			< AmberCeiling => ("AMBER", AssessmentSeverity.Warning,
				$"Line coverage is {line:N1}% — below {AmberCeiling:N0}%."),
			_ => ("BLUE", AssessmentSeverity.Info,
				$"Line coverage is {line:N1}% — below the {GreenThreshold:N0}% needed for GREEN.")
		};

		return Task.FromResult(Band(band, severity, message, line));
	}

	/// <summary>
	/// A failing result carrying the band's own severity rather than the rule's declared one.
	/// </summary>
	/// <remarks>
	/// <see cref="RuleBase.Fail(string, RuleAdvisory)"/> stamps <see cref="Severity"/> onto every
	/// result, which is right for a rule with one outcome and wrong for a graded one. Constructing
	/// the result here is what lets one rule paint three colours.
	/// </remarks>
	private RuleResult Band(string band, AssessmentSeverity severity, string message, double? line)
		=> new()
		{
			RuleId = RuleId,
			RuleName = RuleName,
			Category = Category,
			Severity = severity,
			Passed = false,
			Message = message,
			Advisory = new RuleAdvisory
			{
				Summary = $"Raise line coverage to at least {GreenThreshold:N0}%.",
				Detail = $"""
					This repository is {band}. The estate grades line coverage in four bands: RED for no
					coverage at all, AMBER below {AmberCeiling:N0}%, BLUE below {GreenThreshold:N0}%, and
					GREEN at or above {GreenThreshold:N0}%.

					{(line is null
						? "Codacy holds no coverage for this repository, which usually means CI never uploads any."
						: $"Measured: {line:N1}% line coverage.")}

					Coverage reaches the dashboard through Codacy, so a repository that never uploads is
					RED however well tested it is. The reference implementation is the `coverage` job in
					panoramicdata/PanoramicData.NugetManagement's .github/workflows/ci.yml: it runs the
					test project with `--coverage`, writes Cobertura, and uploads it with
					codacy/codacy-coverage-reporter-action. Copying that job, and adding a
					CODACY_PROJECT_TOKEN secret from the repository's Codacy settings, is the fix.
					""",
				Data = new()
				{
					["band"] = band,
					["measured_line"] = line
				}
			}
		};
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CodeCoverageBandRuleTests"`

Expected: 12 passed.

If `Advisory.Data["measured_line"]` fails to compare against `12.5`, check the declared value type of `RuleAdvisory.Data` and adjust the assertion to match how `CodeCoverageTrendRule` populates the same dictionary — do not change the rule.

- [ ] **Step 5: Commit**

```bash
git add PanoramicData.NugetManagement/Rules/Testing/CodeCoverageBandRule.cs PanoramicData.NugetManagement.Test/CodeCoverageBandRuleTests.cs
git commit -m "feat: grade line coverage on the estate band scale (TST-10)"
```

---

### Task 6: Confirm the registry still builds every rule

`RuleRegistry` instantiates every `IRule` by reflection with `Activator.CreateInstance`. A rule without a public parameterless constructor throws for the whole registry, not just itself, so this deserves its own gate.

**Files:**
- Test: `PanoramicData.NugetManagement.Test/RuleRegistryTests.cs` (create only if it does not already exist)

**Interfaces:**
- Consumes: `CodeCoverageBandRule` from Task 5.
- Produces: nothing consumed downstream.

- [ ] **Step 1: Check whether a registry test already exists**

Run: `pwsh -Command "Get-ChildItem PanoramicData.NugetManagement.Test -Filter 'RuleRegistry*' -Recurse"`

If a test already asserts that every rule is constructible and that rule IDs are unique, skip to Step 4 and just run it.

- [ ] **Step 2: Write the test**

Create `PanoramicData.NugetManagement.Test/RuleRegistryTests.cs`:

```csharp
using AwesomeAssertions;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

public class RuleRegistryTests
{
	[Fact]
	public void EveryRuleIsConstructible()
	{
		// RuleRegistry uses Activator.CreateInstance on every IRule it finds. A rule that needs a
		// constructor argument takes the whole registry down, not just itself.
		var act = () => RuleRegistry.Rules;

		act.Should().NotThrow();
		RuleRegistry.Rules.Should().NotBeEmpty();
	}

	[Fact]
	public void RuleIdsAreUnique()
	{
		var duplicates = RuleRegistry.Rules
			.GroupBy(rule => rule.RuleId, StringComparer.OrdinalIgnoreCase)
			.Where(group => group.Count() > 1)
			.Select(group => group.Key);

		duplicates.Should().BeEmpty();
	}

	[Fact]
	public void TheCoverageBandRuleIsRegistered()
		=> RuleRegistry.Rules.Should().ContainSingle(rule => rule.RuleId == "TST-10");
}
```

- [ ] **Step 3: Run the tests**

Run: `dotnet test --filter "FullyQualifiedName~RuleRegistryTests"`

Expected: 3 passed. A failure on `RuleIdsAreUnique` means TST-10 collided with an existing rule — pick the next free ID and update Task 5's rule and tests to match.

- [ ] **Step 4: Commit**

```bash
git add PanoramicData.NugetManagement.Test/RuleRegistryTests.cs
git commit -m "test: assert every rule is constructible and uniquely identified"
```

---

### Task 7: Full verification

**Files:** none modified.

**Interfaces:**
- Consumes: everything from Tasks 1-6.

- [ ] **Step 1: Stop the dev server**

If the web application is running, stop it. A running instance holds a file lock and the build will silently compile nothing.

- [ ] **Step 2: Run the whole suite**

Run: `dotnet test`

Expected: PASS with no failures. Exit code 5 means an MTP configuration problem rather than a test failure; exit code 1 usually means a file lock; exit code 2 means real failures.

- [ ] **Step 3: Run coverage end to end**

Run: `pwsh ./Run-Coverage.ps1`

Expected: it prints line and branch coverage and writes the Cobertura file. Compare the line figure against the one recorded in Task 1 Step 3 — it should be at least as high, since this plan adds tests and no untested production code beyond the rule itself.

- [ ] **Step 4: Confirm the band the rule would report**

Using the line coverage figure just printed, check it against the bands: RED at 0 or no figure, AMBER below 30, BLUE below 60, GREEN at or above 60. Note which band this repository lands in — it goes in the PR description, and it is the first real answer the whole change exists to produce.

- [ ] **Step 5: Push and open a PR**

```bash
git push -u origin worktree-coverage-codacy-selfhosted
gh pr create --fill
```

The PR body should state the band from Step 4, and note that the `coverage` job's first run on `pdl-prod-cluster` is the real test of Task 2 — the workflow cannot be verified locally.

- [ ] **Step 6: Watch the first CI run**

Run: `gh run watch`

Expected: `build` and `coverage` both pass. If `coverage` fails at the "Install PowerShell" step, the ARC runner's dotnet tool path is not on `PATH` — prefix the next step with `$HOME/.dotnet/tools/pwsh` instead of `pwsh`. If the Codacy upload step reports a failure, it will not fail the job (`continue-on-error`), but check the log: an auth error means `CODACY_PROJECT_TOKEN` is wrong.

- [ ] **Step 7: Confirm Codacy received the coverage**

After a successful run, re-query Codacy:

```bash
curl -s -H "api-token: $CODACY_TOKEN" -H "Accept: application/json" \
  "https://app.codacy.com/api/v3/analysis/organizations/gh/panoramicdata/repositories/PanoramicData.NugetManagement" \
  | tr ',' '\n' | grep -i coverage
```

Expected: `"coverage"` no longer reads `{"status":"None"}` but carries a percentage. Before this change it was `None`, which is the RED this whole plan exists to clear.

---

## Notes for the reviewer

- Tasks 1 and 2 cannot be verified locally beyond a syntax check. The `coverage` job's first run on `pdl-prod-cluster` is its real test, which is why Task 7 Step 6 is part of the plan rather than an afterthought.
- The `publish` job moving to the self-hosted runner is an accepted risk recorded in the spec: NuGet Trusted Publishing needs a GitHub OIDC token, which ARC runners do issue, but this is unproven on this scale set and only exercises on a tag push. If the next release fails to publish, revert that one job to `ubuntu-latest`.
- Task 4 brings TST-07 to life for the first time as a side effect. Expect the first assessment run after this ships to record first coverage baselines estate-wide and pass everywhere; drops are caught from the run after that.
