# Repository Tree Layer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the repository — not the NuGet package — the unit of the navigation tree and of every action the application takes, and stop a failed nuspec lookup from looking like a nuspec that declares no repository.

**Architecture:** Discovery gains a tri-state resolution outcome and a retrying, pooled HTTP fetch. `PackageDashboardRow` is replaced by `RepositoryDashboardRow`, keyed on `RepositoryFullName`, carrying a nested list of the packages that repository publishes. The tree grows a repository layer whose children are a `Packages` branch plus the assessment categories, which is where the rules always applied. Ungoverned packages leave the row set entirely for their own list.

**Tech Stack:** .NET 10, Blazor Server, xUnit v3, AwesomeAssertions, NuGet.Protocol, Octokit, PanoramicData.Blazor (PDTree).

**Spec:** `docs/superpowers/specs/2026-08-29-repository-tree-layer-design.md`

## Global Constraints

- Target framework `net10.0`. `TreatWarningsAsErrors` is enabled in `Directory.Build.props` — a warning fails the build.
- Tabs for indentation. Every public type and member carries an XML doc comment; a missing one is a build error.
- Assertions use **AwesomeAssertions** (`.Should()`). FluentAssertions is banned and rule TST-08 fails the build if referenced.
- Test classes derive from `TestWithOutput(ITestOutputHelper output)`.
- **Run tests with the xunit v3 binary directly.** `dotnet test` reports `Zero tests ran` and exits 5 in this repository even when the suite is healthy:
  ```
  ./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*ClassName"
  ```
  The bare `-class` / `-method` flags print help and run nothing.
- **Before every build, stop the running web app** or the build fails with `MSB3027`:
  ```powershell
  Get-Process -Name "PanoramicData.NugetManagement.Web" -ErrorAction SilentlyContinue | Stop-Process -Force
  ```
- Baseline at branch point: 433 tests, 432 passing. `GitHubIntegrationTests.GitHubAssessment_ThisRepository_ShouldBeCompliant` fails for unrelated reasons (stale GitHub read of PKG-05/PKG-06). Do not attempt to fix it; do not let it mask a new failure.
- Never lower `DiscoveryVersion`. It only goes up.

---

## File Structure

**Created**

| File | Responsibility |
|---|---|
| `Web/Services/RepositoryResolution.cs` | The tri-state outcome of resolving a package's repository |
| `Web/Services/NuspecRepositoryResolver.cs` | Fetching and parsing one nuspec, with retry |
| `Web/Models/RepositoryDashboardRow.cs` | The repository-keyed row and its nested `PublishedPackage` |
| `Web/Models/UngovernedPackage.cs` | A package with no repository we govern, and why |
| `Test/NuspecRepositoryResolverTests.cs` | Tri-state and retry behaviour |
| `Test/StubHttpMessageHandler.cs` | Scripted HTTP responses for tests |
| `Test/RepositoryGroupingTests.cs` | Many packages collapsing to one repository |
| `Test/RepositoryNavTreeTests.cs` | The repository → Packages → categories shape |
| `Test/LookupFailureCarryForwardTests.cs` | A failed lookup never drops a governed repository |

**Modified**

| File | Change |
|---|---|
| `Web/Services/NuGetDiscoveryService.cs` | Delegates to the resolver; parallel; carries the outcome |
| `Web/Services/DashboardService.cs` | Groups packages into repository rows; carry-forward |
| `Web/Services/DashboardCacheService.cs` | Envelope v2; repository- and package-keyed accessors |
| `Web/Services/GovernanceScope.cs` | Operates on repository rows |
| `Web/Services/NavTreeDataProvider.cs` | Repository layer, `Packages` branch, new keys |
| `Web/Services/NavHealthRollup.cs` | Takes repository rows |
| `Web/Services/IssueTreeDataProvider.cs` | Counts repositories, not packages |
| `Web/Services/PackageDashboardDataProvider.cs` | Searches repository name and package ids |
| `Web/Models/NavItem.cs` | `NavView.RepositoryDetail` |
| `Web/Program.cs` | `AddHttpClient` registration |
| `Web/Components/Pages/Home.razor`, `Web/Components/IssuesView.razor` | Row type migration |

---

## Task 1: Tri-state repository resolution with retry

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Services/RepositoryResolution.cs`
- Create: `PanoramicData.NugetManagement.Web/Services/NuspecRepositoryResolver.cs`
- Create: `PanoramicData.NugetManagement.Test/StubHttpMessageHandler.cs`
- Test: `PanoramicData.NugetManagement.Test/NuspecRepositoryResolverTests.cs`

**Interfaces:**
- Consumes: `GitHubRepositoryUrl.Normalize(string?)` — existing, returns canonical `https://github.com/owner/name` or null.
- Produces:
  - `RepositoryResolution` with `RepositoryResolutionOutcome Outcome`, `string? RepositoryUrl`, `string? Error`, and factories `Resolved(string url)`, `NotDeclared()`, `LookupFailed(string error)`.
  - `NuspecRepositoryResolver(IHttpClientFactory httpClientFactory, ILogger<NuspecRepositoryResolver> logger)` with
    `const string HttpClientName = "nuspec"` and
    `Task<RepositoryResolution> ResolveAsync(string packageId, string version, string? projectUrl, CancellationToken cancellationToken)`.
  - `StubHttpClientFactory(HttpMessageHandler handler)` in the test project.

**Why a factory rather than an injected `HttpClient`:** `NuGetDiscoveryService` is a singleton. A typed
client (`AddHttpClient<NuspecRepositoryResolver>`) is registered transient, so the singleton would
capture one instance and its handler for the life of the process — the captive-dependency trap that
pins a DNS entry forever. Asking the factory per call keeps handler rotation working.

- [ ] **Step 1: Write the failing tests**

Create `PanoramicData.NugetManagement.Test/StubHttpMessageHandler.cs`:

```csharp
using System.Net;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Returns a scripted sequence of responses, so a test can describe a flaky endpoint without a
/// network. Each request consumes the next entry; the last entry repeats once exhausted.
/// </summary>
internal sealed class StubHttpMessageHandler(params Func<HttpResponseMessage>[] responses)
	: HttpMessageHandler
{
	private int _callCount;

	/// <summary>How many requests have been made.</summary>
	public int CallCount => _callCount;

	/// <summary>A response carrying the given nuspec body.</summary>
	public static Func<HttpResponseMessage> Nuspec(string body)
		=> () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };

	/// <summary>A response with the given status and no body.</summary>
	public static Func<HttpResponseMessage> Status(HttpStatusCode code)
		=> () => new HttpResponseMessage(code);

	/// <summary>A transport failure, as a dropped connection produces.</summary>
	public static Func<HttpResponseMessage> Throws()
		=> () => throw new HttpRequestException("The connection was closed.");

	/// <inheritdoc />
	protected override Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request,
		CancellationToken cancellationToken)
	{
		var index = Math.Min(_callCount, responses.Length - 1);
		_callCount++;
		return Task.FromResult(responses[index]());
	}
}

/// <summary>
/// Hands out clients over one scripted handler, so the resolver can ask the factory exactly as it
/// does in production.
/// </summary>
internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
	/// <inheritdoc />
	public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
```

Create `PanoramicData.NugetManagement.Test/NuspecRepositoryResolverTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using PanoramicData.NugetManagement.Web.Services;
using System.Net;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the difference between a nuspec that declares no repository and a nuspec we failed to
/// read. Collapsing the two is what filed eight correctly-declared packages as ungoverned, each
/// blamed for an omission it had not made.
/// </summary>
public class NuspecRepositoryResolverTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string DeclaredNuspec = """
		<?xml version="1.0"?>
		<package><metadata>
			<id>ConnectWise.Manage.Api</id>
			<repository type="git" url="https://github.com/panoramicdata/ConnectWise.Manage.Api" />
		</metadata></package>
		""";

	private const string SilentNuspec = """
		<?xml version="1.0"?>
		<package><metadata><id>JiraSetup</id></metadata></package>
		""";

	[Fact]
	public async Task ADeclaredRepositoryShouldResolve()
	{
		var resolution = await ResolveAsync(StubHttpMessageHandler.Nuspec(DeclaredNuspec));

		resolution.Outcome.Should().Be(RepositoryResolutionOutcome.Resolved);
		resolution.RepositoryUrl.Should().Be("https://github.com/panoramicdata/ConnectWise.Manage.Api");
	}

	[Fact]
	public async Task ANuspecDeclaringNothingShouldBeNotDeclared()
	{
		var resolution = await ResolveAsync(StubHttpMessageHandler.Nuspec(SilentNuspec));

		resolution.Outcome.Should().Be(RepositoryResolutionOutcome.NotDeclared);
	}

	[Fact]
	public async Task AGitHubProjectUrlShouldStandInForASilentNuspec()
	{
		var resolution = await ResolveAsync(
			StubHttpMessageHandler.Nuspec(SilentNuspec),
			projectUrl: "https://github.com/panoramicdata/Meraki.Api");

		resolution.Outcome.Should().Be(RepositoryResolutionOutcome.Resolved);
		resolution.RepositoryUrl.Should().Be("https://github.com/panoramicdata/Meraki.Api");
	}

	[Fact]
	public async Task AnUnreadableNuspecShouldBeLookupFailedRatherThanNotDeclared()
	{
		var resolution = await ResolveAsync(StubHttpMessageHandler.Throws());

		resolution.Outcome.Should().Be(
			RepositoryResolutionOutcome.LookupFailed,
			"a package we could not ask about has not been shown to declare nothing");
		resolution.Error.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public async Task ATransientFailureShouldBeRetried()
	{
		var handler = new StubHttpMessageHandler(
			StubHttpMessageHandler.Throws(),
			StubHttpMessageHandler.Throws(),
			StubHttpMessageHandler.Nuspec(DeclaredNuspec));

		var resolution = await ResolveAsync(handler);

		resolution.Outcome.Should().Be(RepositoryResolutionOutcome.Resolved);
		handler.CallCount.Should().Be(3);
	}

	[Fact]
	public async Task ThreeFailuresShouldGiveUp()
	{
		var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Throws());

		var resolution = await ResolveAsync(handler);

		resolution.Outcome.Should().Be(RepositoryResolutionOutcome.LookupFailed);
		handler.CallCount.Should().Be(3, "three attempts, then the answer is that we do not know");
	}

	[Fact]
	public async Task AMissingNuspecShouldNotBeRetried()
	{
		var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.NotFound));

		var resolution = await ResolveAsync(handler);

		resolution.Outcome.Should().Be(RepositoryResolutionOutcome.NotDeclared);
		handler.CallCount.Should().Be(1, "a 404 is a definite answer, not a flaky one");
	}

	private static Task<RepositoryResolution> ResolveAsync(
		Func<HttpResponseMessage> response,
		string? projectUrl = null)
		=> ResolveAsync(new StubHttpMessageHandler(response), projectUrl);

	private static Task<RepositoryResolution> ResolveAsync(
		StubHttpMessageHandler handler,
		string? projectUrl = null)
	{
		var resolver = new NuspecRepositoryResolver(
			new StubHttpClientFactory(handler),
			NullLogger<NuspecRepositoryResolver>.Instance)
		{
			RetryDelay = TimeSpan.Zero
		};

		return resolver.ResolveAsync("ConnectWise.Manage.Api", "3.1.0", projectUrl, CancellationToken.None);
	}
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```
Get-Process -Name "PanoramicData.NugetManagement.Web" -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build PanoramicData.NugetManagement.slnx
```

Expected: the build FAILS with `CS0246: The type or namespace name 'NuspecRepositoryResolver' could not be found`.

- [ ] **Step 3: Write the resolution type**

Create `PanoramicData.NugetManagement.Web/Services/RepositoryResolution.cs`:

```csharp
namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// What came of asking where a package's source lives.
/// </summary>
public enum RepositoryResolutionOutcome
{
	/// <summary>The package names a GitHub repository.</summary>
	Resolved,

	/// <summary>The nuspec was read and names no GitHub repository.</summary>
	NotDeclared,

	/// <summary>The nuspec could not be read, so nothing is known either way.</summary>
	LookupFailed
}

/// <summary>
/// Where a package's source lives, or why we cannot say.
/// </summary>
/// <remarks>
/// The distinction between <see cref="RepositoryResolutionOutcome.NotDeclared"/> and
/// <see cref="RepositoryResolutionOutcome.LookupFailed"/> is the whole point of this type. Both were
/// once a null string, so a dropped connection was recorded as a fact about somebody's nuspec, and
/// eight repositories that declare themselves perfectly well were reported as declaring nothing.
/// </remarks>
public sealed class RepositoryResolution
{
	private RepositoryResolution(RepositoryResolutionOutcome outcome, string? repositoryUrl, string? error)
	{
		Outcome = outcome;
		RepositoryUrl = repositoryUrl;
		Error = error;
	}

	/// <summary>What came of the lookup.</summary>
	public RepositoryResolutionOutcome Outcome { get; }

	/// <summary>The canonical repository URL, set only when <see cref="Outcome"/> is Resolved.</summary>
	public string? RepositoryUrl { get; }

	/// <summary>Why the lookup failed, set only when <see cref="Outcome"/> is LookupFailed.</summary>
	public string? Error { get; }

	/// <summary>The package names the given GitHub repository.</summary>
	public static RepositoryResolution Resolved(string repositoryUrl)
		=> new(RepositoryResolutionOutcome.Resolved, repositoryUrl, null);

	/// <summary>The nuspec was read and names no GitHub repository.</summary>
	public static RepositoryResolution NotDeclared()
		=> new(RepositoryResolutionOutcome.NotDeclared, null, null);

	/// <summary>The nuspec could not be read.</summary>
	public static RepositoryResolution LookupFailed(string error)
		=> new(RepositoryResolutionOutcome.LookupFailed, null, error);
}
```

- [ ] **Step 4: Write the resolver**

Create `PanoramicData.NugetManagement.Web/Services/NuspecRepositoryResolver.cs`:

```csharp
using System.Net;
using System.Xml;
using System.Xml.Linq;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Reads where a package's source lives from its nuspec, retrying a fetch that fails in transit.
/// </summary>
/// <remarks>
/// The nuspec's <c>repository</c> element first, because that is the publisher saying where the
/// source is. <c>projectUrl</c> is a documentation link and need not be the source at all.
///
/// Retries matter more than they look. Discovery asks this question once per package — a hundred-odd
/// small requests in a burst — and a single-attempt fetch turned any one of them going astray into a
/// permanent-looking claim that the nuspec declared nothing.
/// </remarks>
public class NuspecRepositoryResolver(
	IHttpClientFactory httpClientFactory,
	ILogger<NuspecRepositoryResolver> logger)
{
	private const int MaxAttempts = 3;

	/// <summary>The name of the configured client this resolver asks for.</summary>
	public const string HttpClientName = "nuspec";

	/// <summary>
	/// The base delay between attempts, doubled each time. Settable so tests need not wait.
	/// </summary>
	public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);

	/// <summary>
	/// Where the package's source lives, or why we cannot say.
	/// </summary>
	/// <param name="packageId">The package identifier.</param>
	/// <param name="version">The version whose nuspec to read.</param>
	/// <param name="projectUrl">The package's project URL, used only when the nuspec declares nothing.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	public async Task<RepositoryResolution> ResolveAsync(
		string packageId,
		string version,
		string? projectUrl,
		CancellationToken cancellationToken)
	{
		var id = packageId.ToLowerInvariant();
		var url = $"https://api.nuget.org/v3-flatcontainer/{id}/{version.ToLowerInvariant()}/{id}.nuspec";

		using var client = httpClientFactory.CreateClient(HttpClientName);

		string? nuspec = null;
		string? lastError = null;

		for (var attempt = 1; attempt <= MaxAttempts; attempt++)
		{
			try
			{
				using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

				// A 404 is the source saying there is nothing there. Asking twice more cannot change it,
				// and treating it as flaky would spend three requests on every unlisted package.
				if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
				{
					nuspec = null;
					lastError = null;
					break;
				}

				response.EnsureSuccessStatusCode();
				nuspec = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
				lastError = null;
				break;
			}
			catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
				&& !cancellationToken.IsCancellationRequested)
			{
				lastError = ex.Message;
				logger.LogDebug(
					ex,
					"Attempt {Attempt} of {MaxAttempts} to read the nuspec for {PackageId} {Version} failed",
					attempt,
					MaxAttempts,
					packageId,
					version);

				if (attempt < MaxAttempts && RetryDelay > TimeSpan.Zero)
				{
					await Task.Delay(RetryDelay * attempt, cancellationToken).ConfigureAwait(false);
				}
			}
		}

		if (lastError is not null)
		{
			return RepositoryResolution.LookupFailed(lastError);
		}

		var declared = nuspec is null ? null : ReadRepositoryUrl(nuspec, packageId, version);

		return GitHubRepositoryUrl.Normalize(declared) is { } fromNuspec
			? RepositoryResolution.Resolved(fromNuspec)
			: GitHubRepositoryUrl.Normalize(projectUrl) is { } fromProject
				? RepositoryResolution.Resolved(fromProject)
				: RepositoryResolution.NotDeclared();
	}

	private string? ReadRepositoryUrl(string nuspec, string packageId, string version)
	{
		try
		{
			return XDocument.Parse(nuspec)
				.Descendants()
				.FirstOrDefault(element =>
					string.Equals(element.Name.LocalName, "repository", StringComparison.OrdinalIgnoreCase))
				?.Attribute("url")?.Value;
		}
		catch (XmlException ex)
		{
			// Malformed XML is the publisher's, not ours, and is a fact about the nuspec rather than a
			// failure to read it: there is nothing to retry.
			logger.LogDebug(ex, "The nuspec for {PackageId} {Version} is not well-formed XML", packageId, version);
			return null;
		}
	}
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```
Get-Process -Name "PanoramicData.NugetManagement.Web" -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build PanoramicData.NugetManagement.slnx
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*NuspecRepositoryResolverTests"
```

Expected: `total: 7, failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Services/RepositoryResolution.cs PanoramicData.NugetManagement.Web/Services/NuspecRepositoryResolver.cs PanoramicData.NugetManagement.Test/NuspecRepositoryResolverTests.cs PanoramicData.NugetManagement.Test/StubHttpMessageHandler.cs
git commit -m "Tell a nuspec that declares nothing from one we could not read"
```

---

## Task 2: Discovery uses the resolver, pooled and in parallel

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Services/NuGetDiscoveryService.cs`
- Modify: `PanoramicData.NugetManagement.Web/Program.cs:21`
- Test: `PanoramicData.NugetManagement.Test/NuspecRepositoryResolverTests.cs` (unchanged; regression cover)

**Interfaces:**
- Consumes: `NuspecRepositoryResolver.ResolveAsync(...)` from Task 1.
- Produces: `NuGetPackageInfo` gains `RepositoryResolutionOutcome ResolutionOutcome { get; init; }` and `string? ResolutionError { get; init; }`. `RepositoryOwner` and `RepositoryName` remain, derived via `GitHubRepositoryUrl`.

- [ ] **Step 1: Add the outcome to `NuGetPackageInfo`**

In `NuGetDiscoveryService.cs`, inside `NuGetPackageInfo` (after `RepositoryName`, around line 234):

```csharp
	/// <summary>
	/// What came of resolving <see cref="RepositoryUrl"/>. A package with no repository is only
	/// ungoverned for a stated reason when this says the nuspec was actually read.
	/// </summary>
	public RepositoryResolutionOutcome ResolutionOutcome { get; init; } = RepositoryResolutionOutcome.NotDeclared;

	/// <summary>
	/// Why resolution failed, when <see cref="ResolutionOutcome"/> is
	/// <see cref="RepositoryResolutionOutcome.LookupFailed"/>.
	/// </summary>
	public string? ResolutionError { get; init; }
```

- [ ] **Step 2: Inject the resolver**

Change the `NuGetDiscoveryService` constructor to take `NuspecRepositoryResolver resolver` alongside its existing `ILogger`. Delete the private `ResolveRepositoryUrlAsync`, `ReadRepositoryUrlFromNuspecAsync`, `ExtractRepoOwner` and `ExtractRepoName` members — `GitHubRepositoryUrl` and the resolver now own all of that.

- [ ] **Step 3: Resolve the batch in parallel**

Replace the `foreach (var result in batch)` loop in `DiscoverOrganizationPackagesAsync` with:

```csharp
			// One small request per package. Sequentially that is a hundred-odd round trips in series;
			// throttled at eight it is a few seconds, and the throttle is what keeps a burst from
			// looking like an attack to the source.
			var resolved = new NuGetPackageInfo[batch.Count];

			await Parallel.ForEachAsync(
				Enumerable.Range(0, batch.Count),
				new ParallelOptions
				{
					MaxDegreeOfParallelism = 8,
					CancellationToken = cancellationToken
				},
				async (index, token) =>
				{
					var result = batch[index];
					var resolution = await _resolver.ResolveAsync(
						result.Identity.Id,
						result.Identity.Version.ToNormalizedString(),
						result.ProjectUrl?.ToString(),
						token).ConfigureAwait(false);

					resolved[index] = new NuGetPackageInfo
					{
						PackageId = result.Identity.Id,
						LatestVersion = result.Identity.Version.ToNormalizedString(),
						Organization = owner,
						RepositoryUrl = resolution.RepositoryUrl,
						RepositoryOwner = GitHubRepositoryUrl.Owner(resolution.RepositoryUrl),
						RepositoryName = GitHubRepositoryUrl.Name(resolution.RepositoryUrl),
						ResolutionOutcome = resolution.Outcome,
						ResolutionError = resolution.Error
					};
				}).ConfigureAwait(false);

			results.AddRange(resolved);
```

- [ ] **Step 4: Log what could not be resolved**

Immediately before the closing `LogInformation("Found {Count} packages...")`:

```csharp
		var unresolved = results
			.Where(p => p.ResolutionOutcome is RepositoryResolutionOutcome.LookupFailed)
			.Select(p => p.PackageId)
			.ToList();

		if (unresolved.Count > 0)
		{
			logger.LogWarning(
				"Could not read the nuspec for {Count} package(s) of '{Owner}': {Packages}. Their repositories are unchanged from the last successful discovery; rediscover to try again.",
				unresolved.Count,
				owner,
				string.Join(", ", unresolved));
		}
```

- [ ] **Step 5: Register the HTTP client**

In `Program.cs`, replace line 21 (`builder.Services.AddSingleton<NuGetDiscoveryService>();`) with:

```csharp
// A pooled handler rather than a client per package: discovery makes a hundred-odd nuspec requests
// in a burst, and a fresh HttpClient each time leaks a socket per package. Named rather than typed,
// because the resolver is consumed by a singleton and a typed client is transient.
builder.Services.AddHttpClient(NuspecRepositoryResolver.HttpClientName, client =>
{
	client.Timeout = TimeSpan.FromSeconds(15);
	client.DefaultRequestHeaders.UserAgent.ParseAdd("PanoramicData.NugetManagement");
});
builder.Services.AddSingleton<NuspecRepositoryResolver>();
builder.Services.AddSingleton<NuGetDiscoveryService>();
```

- [ ] **Step 6: Build and run the whole suite**

```
Get-Process -Name "PanoramicData.NugetManagement.Web" -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build PanoramicData.NugetManagement.slnx
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe
```

Expected: `Build succeeded`, and `failed: 1` — only the pre-existing `GitHubAssessment_ThisRepository_ShouldBeCompliant`.

- [ ] **Step 7: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Services/NuGetDiscoveryService.cs PanoramicData.NugetManagement.Web/Program.cs
git commit -m "Ask for a hundred nuspecs on one pooled client, eight at a time"
```

---

## Task 3: The repository row

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Models/RepositoryDashboardRow.cs`
- Create: `PanoramicData.NugetManagement.Web/Models/UngovernedPackage.cs`
- Test: `PanoramicData.NugetManagement.Test/RepositoryRowTests.cs`

**Interfaces:**
- Produces:
  - `PublishedPackage` — `{ string PackageId, string? LatestVersion }` plus `bool? MatchesTag(string? latestTag)`.
  - `RepositoryDashboardRow` — every member of the old `PackageDashboardRow` except `PackageId`, `LatestVersion` and `NuGetVersionMatchesTag`, with `required string RepositoryFullName`, `List<PublishedPackage> Packages`, and `bool AnyPackageOutOfStepWithTag`.
  - `UngovernedPackage` — `{ required string PackageId, string Organization, string? DeclaredRepository, required string Reason }`.
- Note for later tasks: `HealthStatus`, `TotalFailures`, `TotalCriticals`, `TotalErrors`, `TotalWarnings`, `CategorySummaries`, `Status`, `StatusMessage`, `IsReassessing`, `Assessment`, `IsGoverned`, `NotGovernedReason`, `IsClonedLocally`, `LocalPath`, `SlnxPath`, `IsWorkingTreeClean`, `CurrentBranch`, `IsSyncedWithOrigin`, `SyncStatusCheckedAtUtc`, `LatestTag`, `RepositoryUrl`, `Organization` all keep their existing names and types. `PackageHealthStatus`, `CategorySummary` and `PackageStatus` stay in `PackageDashboardRow.cs` and are reused unchanged.

- [ ] **Step 1: Write the failing test**

Create `PanoramicData.NugetManagement.Test/RepositoryRowTests.cs`:

```csharp
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the row that is a repository rather than a package. A repository publishing four
/// packages has four versions, and the row that flattened them to one could only ever be right
/// about the first.
/// </summary>
public class RepositoryRowTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void APackageMatchingTheTagShouldSaySo()
		=> new PublishedPackage { PackageId = "A", LatestVersion = "1.4.2" }
			.MatchesTag("1.4.2").Should().BeTrue();

	[Fact]
	public void APackageBehindTheTagShouldSaySo()
		=> new PublishedPackage { PackageId = "A", LatestVersion = "1.4.0" }
			.MatchesTag("1.4.2").Should().BeFalse();

	[Fact]
	public void APackageWithNoKnownTagShouldSayNothing()
		=> new PublishedPackage { PackageId = "A", LatestVersion = "1.4.0" }
			.MatchesTag(null).Should().BeNull();

	[Fact]
	public void ARepositoryShouldReportWhenAnyOfItsPackagesIsOutOfStep()
	{
		var row = new RepositoryDashboardRow
		{
			RepositoryFullName = "panoramicdata/PanoramicData.ECharts",
			LatestTag = "1.4.2",
			Packages =
			[
				new() { PackageId = "PanoramicData.ECharts", LatestVersion = "1.4.2" },
				new() { PackageId = "PanoramicData.ECharts.Samples", LatestVersion = "1.4.0" }
			]
		};

		row.AnyPackageOutOfStepWithTag.Should().BeTrue();
	}

	[Fact]
	public void ARepositoryWhosePackagesAllMatchShouldBeInStep()
	{
		var row = new RepositoryDashboardRow
		{
			RepositoryFullName = "panoramicdata/Meraki.Api",
			LatestTag = "1.4.2",
			Packages = [new() { PackageId = "Meraki.Api", LatestVersion = "1.4.2" }]
		};

		row.AnyPackageOutOfStepWithTag.Should().BeFalse();
	}

	[Fact]
	public void AnUnassessedRepositoryShouldBeUnknown()
		=> new RepositoryDashboardRow { RepositoryFullName = "panoramicdata/Meraki.Api" }
			.HealthStatus.Should().Be(PackageHealthStatus.Unknown);
}
```

- [ ] **Step 2: Run to verify it fails**

```
dotnet build PanoramicData.NugetManagement.slnx
```

Expected: FAIL, `CS0246: The type or namespace name 'RepositoryDashboardRow' could not be found`.

- [ ] **Step 3: Write the models**

Create `PanoramicData.NugetManagement.Web/Models/UngovernedPackage.cs`:

```csharp
namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// A package we publish whose source is not a repository we govern, and why.
/// </summary>
/// <remarks>
/// Kept out of the repository rows rather than shoehorned into one. A row that stands for a
/// repository and holds no repository is a contradiction every consumer then has to guard against;
/// these have their own list and their own branch of the tree, counted in nothing.
/// </remarks>
public class UngovernedPackage
{
	/// <summary>The NuGet package identifier.</summary>
	public required string PackageId { get; init; }

	/// <summary>The organisation the package was discovered under.</summary>
	public string Organization { get; init; } = string.Empty;

	/// <summary>The repository the nuspec declared, when it declared one we do not govern.</summary>
	public string? DeclaredRepository { get; init; }

	/// <summary>Why this package is not governed. Names the nuspec that needs correcting.</summary>
	public required string Reason { get; init; }
}
```

Create `PanoramicData.NugetManagement.Web/Models/RepositoryDashboardRow.cs`. Copy `PackageDashboardRow` wholesale, then apply exactly these changes:

1. Rename the class to `RepositoryDashboardRow`; leave `PackageHealthStatus`, `CategorySummary` and `PackageStatus` where they are in `PackageDashboardRow.cs`.
2. Delete `PackageId`, `LatestVersion` and `NuGetVersionMatchesTag`.
3. Make `RepositoryFullName` `required string` rather than `string?`.
4. Add, after `RepositoryFullName`:

```csharp
	/// <summary>
	/// The packages this repository publishes. A repository can publish many —
	/// PanoramicData.ECharts publishes four — and they version independently, so the version and the
	/// tag comparison belong here rather than on the repository.
	/// </summary>
	public List<PublishedPackage> Packages { get; set; } = [];

	/// <summary>
	/// Whether any published package is at a version other than the repository's latest tag.
	/// </summary>
	public bool AnyPackageOutOfStepWithTag
		=> Packages.Any(package => package.MatchesTag(LatestTag) == false);
```

5. Add the nested-record type at the end of the same file:

```csharp
/// <summary>
/// One NuGet package published by a repository.
/// </summary>
public class PublishedPackage
{
	/// <summary>The NuGet package identifier.</summary>
	public required string PackageId { get; init; }

	/// <summary>The latest published version on NuGet.</summary>
	public string? LatestVersion { get; set; }

	/// <summary>
	/// Whether this package's published version matches the repository's latest tag, or null when
	/// either is unknown.
	/// </summary>
	/// <param name="latestTag">The repository's latest git tag.</param>
	public bool? MatchesTag(string? latestTag)
		=> LatestVersion is not null && latestTag is not null
			? string.Equals(LatestVersion, latestTag, StringComparison.OrdinalIgnoreCase)
			: null;
}
```

- [ ] **Step 4: Run to verify it passes**

```
dotnet build PanoramicData.NugetManagement.slnx
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*RepositoryRowTests"
```

Expected: `total: 6, failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Models/RepositoryDashboardRow.cs PanoramicData.NugetManagement.Web/Models/UngovernedPackage.cs PanoramicData.NugetManagement.Test/RepositoryRowTests.cs
git commit -m "Give a repository a row of its own, and its packages a place in it"
```

---

## Task 4: Cache envelope version 2

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Services/DashboardCacheService.cs`
- Test: `PanoramicData.NugetManagement.Test/DashboardCacheVersionTests.cs`

**Interfaces:**
- Consumes: `RepositoryDashboardRow`, `UngovernedPackage` from Task 3.
- Produces, replacing the package-keyed members:
  - `List<RepositoryDashboardRow>? GetCachedRows()`
  - `void SetRows(List<RepositoryDashboardRow> rows)`
  - `void Update(List<RepositoryDashboardRow> rows)`
  - `RepositoryDashboardRow? GetRow(string repositoryFullName)` — case-insensitive
  - `void UpsertRow(RepositoryDashboardRow row)`
  - `bool RemoveRow(string repositoryFullName)`
  - `IReadOnlyList<UngovernedPackage> GetUngovernedPackages()`
  - `void SetUngovernedPackages(List<UngovernedPackage> packages)`
  - `void NotifyRowUpdated()` — unchanged
  - `RepositoryDashboardRow? GetRowByPackageId(string packageId)` — for callers that still hold a package id

- [ ] **Step 1: Update the version test**

In `DashboardCacheVersionTests.cs`, change every literal `1` that stands for the discovery version to `2`, and add:

```csharp
	[Fact]
	public void ACacheWrittenBeforeTheRepositoryLayerShouldBeDiscarded()
	{
		var directory = Path.Combine(Path.GetTempPath(), "nugetmanagement-tests", Guid.NewGuid().ToString("n"));
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, "dashboard-cache.json");

		File.WriteAllText(path, """
			{"discoveryVersion":1,"lastRefreshUtc":"2026-08-29T00:00:00+00:00","rows":[
			  {"packageId":"Meraki.Api","repositoryFullName":"panoramicdata/Meraki.Api"}]}
			""");

		new DashboardCacheService(NullLogger<DashboardCacheService>.Instance, path)
			.GetCachedRows()
			.Should().BeNull("rows keyed on a package cannot be read as repositories");

		Directory.Delete(directory, recursive: true);
	}
```

- [ ] **Step 2: Run to verify it fails**

```
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*DashboardCacheVersionTests"
```

Expected: FAIL — the service still reports version 1.

- [ ] **Step 3: Bump the version and widen the envelope**

In `DashboardCacheService.cs`:

```csharp
	/// 1: repositories outside the configured organisations are no longer governed.
	/// 2: the row is the repository, not the package, and ungoverned packages are held separately.
	public const int DiscoveryVersion = 2;
```

Change `_cachedRows` to `List<RepositoryDashboardRow>?`, add `private List<UngovernedPackage> _ungovernedPackages = [];`, and change every member listed under **Interfaces** above to the repository-keyed signature. `GetRow` and `RemoveRow` compare with `StringComparer.OrdinalIgnoreCase`. Add:

```csharp
	/// <summary>
	/// The cached repository whose packages include the given id, or null when none does.
	/// </summary>
	/// <remarks>
	/// For callers that still hold a package id — a remediation prompt, a deep link — now that the row
	/// they want is keyed on the repository that publishes it.
	/// </remarks>
	public RepositoryDashboardRow? GetRowByPackageId(string packageId)
	{
		lock (_lock)
		{
			return _cachedRows?.FirstOrDefault(row => row.Packages
				.Any(package => string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase)));
		}
	}
```

Extend `CacheEnvelope` with `public List<UngovernedPackage> UngovernedPackages { get; set; } = [];` and persist it in the save path alongside `Rows`.

- [ ] **Step 4: Run to verify it passes**

```
dotnet build PanoramicData.NugetManagement.slnx
```

The build will now fail in `DashboardService`, `NavTreeDataProvider`, `GovernanceScope`, `NavHealthRollup`, `PackageDashboardDataProvider`, `IssuesView.razor` and `Home.razor`. **That is expected** — Tasks 5 to 10 fix them in order. Confirm the only errors are `CS1503`/`CS0117`/`CS1061` about `PackageDashboardRow` vs `RepositoryDashboardRow`, then move on without committing.

---

## Task 5: Discovery builds repository rows

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Services/DashboardService.cs:51-104`
- Test: `PanoramicData.NugetManagement.Test/RepositoryGroupingTests.cs`
- Test: `PanoramicData.NugetManagement.Test/LookupFailureCarryForwardTests.cs`

**Interfaces:**
- Consumes: `NuGetPackageInfo.ResolutionOutcome` (Task 2), `RepositoryDashboardRow`/`PublishedPackage`/`UngovernedPackage` (Task 3), cache accessors (Task 4).
- Produces: `DashboardService.BuildRows(IReadOnlyList<NuGetPackageInfo> packages, IReadOnlyList<RepositoryDashboardRow> previousRows, IReadOnlyList<string> organizations)` returning `(List<RepositoryDashboardRow> Rows, List<UngovernedPackage> Ungoverned)`. Made `internal static` and exposed to the test project so grouping can be tested without a network.

- [ ] **Step 1: Make the test project a friend**

In `PanoramicData.NugetManagement.Web.csproj`, inside the first `<PropertyGroup>`:

```xml
	<InternalsVisibleTo Include="PanoramicData.NugetManagement.Test" />
```

- [ ] **Step 2: Write the failing grouping test**

Create `PanoramicData.NugetManagement.Test/RepositoryGroupingTests.cs`:

```csharp
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that many packages from one repository make one row. PanoramicData.ECharts publishes four,
/// and until now each was cloned, assessed and remediated separately — the same repository, the same
/// findings, four times over.
/// </summary>
public class RepositoryGroupingTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly string[] _organizations = ["panoramicdata"];

	[Fact]
	public void FourPackagesFromOneRepositoryShouldMakeOneRow()
		=> Build(EChartsPackages()).Rows.Should().ContainSingle()
			.Which.RepositoryFullName.Should().Be("panoramicdata/PanoramicData.ECharts");

	[Fact]
	public void TheRowShouldListEveryPackageItPublishes()
		=> Build(EChartsPackages()).Rows.Single().Packages
			.Select(package => package.PackageId)
			.Should().BeEquivalentTo(
			[
				"PanoramicData.ECharts",
				"PanoramicData.ECharts.BindingGenerator",
				"PanoramicData.ECharts.Samples",
				"PanoramicData.ECharts.Sandbox"
			]);

	[Fact]
	public void EachPackageShouldKeepItsOwnVersion()
		=> Build(EChartsPackages()).Rows.Single().Packages
			.Single(package => package.PackageId == "PanoramicData.ECharts.Samples")
			.LatestVersion.Should().Be("1.4.0");

	[Fact]
	public void RepositoriesShouldBeGroupedRegardlessOfCase()
	{
		var packages = new List<NuGetPackageInfo>
		{
			Package("MagicSuite.Api", "https://github.com/panoramicdata/MagicSuite", "2.0.0"),
			Package("MagicSuite.Client", "https://github.com/PanoramicData/magicsuite", "2.0.1")
		};

		Build(packages).Rows.Should().ContainSingle("owner/name differing only in case is one repository");
	}

	[Fact]
	public void APackageDeclaringNothingShouldBeUngovernedRatherThanARow()
	{
		var result = Build([Package("JiraSetup", repositoryUrl: null, "1.0.0")]);

		result.Rows.Should().BeEmpty();
		result.Ungoverned.Should().ContainSingle()
			.Which.Reason.Should().Contain("declares no repository");
	}

	private static (List<RepositoryDashboardRow> Rows, List<UngovernedPackage> Ungoverned) Build(
		List<NuGetPackageInfo> packages)
		=> DashboardService.BuildRows(packages, [], _organizations);

	private static List<NuGetPackageInfo> EChartsPackages() =>
	[
		Package("PanoramicData.ECharts", "https://github.com/panoramicdata/PanoramicData.ECharts", "1.4.2"),
		Package("PanoramicData.ECharts.BindingGenerator", "https://github.com/panoramicdata/PanoramicData.ECharts", "1.4.2"),
		Package("PanoramicData.ECharts.Samples", "https://github.com/panoramicdata/PanoramicData.ECharts", "1.4.0"),
		Package("PanoramicData.ECharts.Sandbox", "https://github.com/panoramicdata/PanoramicData.ECharts", "1.4.2")
	];

	private static NuGetPackageInfo Package(string packageId, string? repositoryUrl, string version)
		=> new()
		{
			PackageId = packageId,
			LatestVersion = version,
			Organization = "panoramicdata",
			RepositoryUrl = repositoryUrl,
			RepositoryOwner = GitHubRepositoryUrl.Owner(repositoryUrl),
			RepositoryName = GitHubRepositoryUrl.Name(repositoryUrl),
			ResolutionOutcome = repositoryUrl is null
				? RepositoryResolutionOutcome.NotDeclared
				: RepositoryResolutionOutcome.Resolved
		};
}
```

The spec's "a repository publishing three packages is assessed exactly once" needs no separate test:
the assessment loop iterates rows, and `FourPackagesFromOneRepositoryShouldMakeOneRow` asserts there
is one row. Assessing twice is no longer representable, which is the point of the model change.

Create `PanoramicData.NugetManagement.Test/LookupFailureCarryForwardTests.cs`:

```csharp
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that a bad afternoon on nuget.org cannot shrink the estate. A repository governed yesterday
/// must not disappear because one small request went astray, and must never be blamed for an
/// omission its nuspec did not make.
/// </summary>
public class LookupFailureCarryForwardTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly string[] _organizations = ["panoramicdata"];

	[Fact]
	public void AFailedLookupShouldKeepTheRepositoryItHadYesterday()
	{
		var result = DashboardService.BuildRows([Unreadable("ConnectWise.Manage.Api")], Previously(), _organizations);

		result.Rows.Should().ContainSingle()
			.Which.RepositoryFullName.Should().Be("panoramicdata/ConnectWise.Manage.Api");
		result.Ungoverned.Should().BeEmpty();
	}

	[Fact]
	public void AFailedLookupWithNothingToFallBackOnShouldNotBlameTheNuspec()
	{
		var result = DashboardService.BuildRows([Unreadable("Brand.New.Api")], [], _organizations);

		result.Rows.Should().BeEmpty();
		var ungoverned = result.Ungoverned.Should().ContainSingle().Subject;
		ungoverned.Reason.Should().Contain("Could not read the nuspec");
		ungoverned.Reason.Should().NotContain(
			"declares no repository",
			"we did not read the nuspec, so we cannot say what it declares");
	}

	private static List<RepositoryDashboardRow> Previously() =>
	[
		new()
		{
			RepositoryFullName = "panoramicdata/ConnectWise.Manage.Api",
			Organization = "panoramicdata",
			Packages = [new() { PackageId = "ConnectWise.Manage.Api", LatestVersion = "3.0.74" }]
		}
	];

	private static NuGetPackageInfo Unreadable(string packageId)
		=> new()
		{
			PackageId = packageId,
			LatestVersion = "3.1.0",
			Organization = "panoramicdata",
			ResolutionOutcome = RepositoryResolutionOutcome.LookupFailed,
			ResolutionError = "The connection was closed."
		};
}
```

- [ ] **Step 3: Run to verify they fail**

```
dotnet build PanoramicData.NugetManagement.slnx
```

Expected: FAIL, `CS0117: 'DashboardService' does not contain a definition for 'BuildRows'`.

- [ ] **Step 4: Write `BuildRows` and rewire discovery**

Add to `DashboardService`:

```csharp
	/// <summary>
	/// Turns discovered packages into one row per repository, plus the packages that belong to no
	/// repository we govern.
	/// </summary>
	/// <remarks>
	/// Static and free of the network so the grouping — the part with the interesting edge cases —
	/// can be tested without one.
	/// </remarks>
	/// <param name="packages">The packages discovered from NuGet.</param>
	/// <param name="previousRows">The rows from the last successful discovery, for carry-forward.</param>
	/// <param name="organizations">The organisations under management.</param>
	internal static (List<RepositoryDashboardRow> Rows, List<UngovernedPackage> Ungoverned) BuildRows(
		IReadOnlyList<NuGetPackageInfo> packages,
		IReadOnlyList<RepositoryDashboardRow> previousRows,
		IReadOnlyList<string> organizations)
	{
		// A package whose nuspec we could not read keeps the repository we knew it by. Anything else
		// removes a repository from governance because a request went astray.
		var previousByPackageId = previousRows
			.SelectMany(row => row.Packages.Select(package => (package.PackageId, row.RepositoryFullName)))
			.GroupBy(pair => pair.PackageId, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(group => group.Key, group => group.First().RepositoryFullName, StringComparer.OrdinalIgnoreCase);

		var rows = new Dictionary<string, RepositoryDashboardRow>(StringComparer.OrdinalIgnoreCase);
		var ungoverned = new List<UngovernedPackage>();

		foreach (var package in packages)
		{
			var identity = IdentifyRepository(package, previousByPackageId);

			var reason = identity is null
				? ReasonForNoRepository(package)
				: GovernanceScope.ReasonNotGoverned(identity, organizations);

			if (reason is not null)
			{
				ungoverned.Add(new UngovernedPackage
				{
					PackageId = package.PackageId,
					Organization = package.Organization,
					DeclaredRepository = identity,
					Reason = reason
				});
				continue;
			}

			if (!rows.TryGetValue(identity!, out var row))
			{
				row = new RepositoryDashboardRow
				{
					RepositoryFullName = identity!,
					Organization = package.Organization,
					RepositoryUrl = package.RepositoryUrl ?? $"https://github.com/{identity}"
				};
				rows[identity!] = row;
			}

			row.Packages.Add(new PublishedPackage
			{
				PackageId = package.PackageId,
				LatestVersion = package.LatestVersion
			});
		}

		foreach (var row in rows.Values)
		{
			row.Packages.Sort((left, right) =>
				string.Compare(left.PackageId, right.PackageId, StringComparison.OrdinalIgnoreCase));
		}

		return ([.. rows.Values.OrderBy(row => row.RepositoryFullName, StringComparer.OrdinalIgnoreCase)], ungoverned);
	}

	private static string? IdentifyRepository(
		NuGetPackageInfo package,
		Dictionary<string, string> previousByPackageId)
	{
		if (package.RepositoryName is not null)
		{
			return $"{package.RepositoryOwner ?? package.Organization}/{package.RepositoryName}";
		}

		return package.ResolutionOutcome is RepositoryResolutionOutcome.LookupFailed
			&& previousByPackageId.TryGetValue(package.PackageId, out var previous)
				? previous
				: null;
	}

	private static string ReasonForNoRepository(NuGetPackageInfo package)
		=> package.ResolutionOutcome is RepositoryResolutionOutcome.LookupFailed
			? "Could not read the nuspec (network) — rediscover to try again."
			: "The package declares no repository in its nuspec.";
```

Then replace everything in `DiscoverPackagesAsync` from `var rows = new List<PackageDashboardRow>();`
to the `return rows;` with:

```csharp
		var (rows, ungoverned) = BuildRows(
			packages,
			_cache.GetCachedRows() ?? [],
			_runtimeSettings.Organizations);

		_cache.SetUngovernedPackages(ungoverned);

		foreach (var row in rows)
		{
			var isCloned = _localRepo.IsClonedLocally(row.RepositoryFullName);

			row.IsClonedLocally = isCloned;
			row.LocalPath = _localRepo.GetLocalPath(row.RepositoryFullName);
			row.SlnxPath = isCloned ? _localRepo.FindSlnxFile(row.RepositoryFullName) : null;
			row.Status = isCloned ? PackageStatus.NotAssessed : PackageStatus.NotCloned;

			if (isCloned)
			{
				row.CurrentBranch = await _localRepo
					.GetCurrentBranchAsync(row.RepositoryFullName, cancellationToken)
					.ConfigureAwait(false);

				row.IsWorkingTreeClean = await _localRepo
					.IsWorkingTreeCleanAsync(row.RepositoryFullName, cancellationToken)
					.ConfigureAwait(false);
			}
		}

		return rows;
```

Change the method's return type to `Task<List<RepositoryDashboardRow>>`.

- [ ] **Step 5: Run to verify they pass**

```
dotnet build PanoramicData.NugetManagement.slnx
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*RepositoryGroupingTests"
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*LookupFailureCarryForwardTests"
```

Expected: `failed: 0` for both. Other call sites still fail to compile — continue.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Group the packages of one repository into one row"
```

---

## Task 6: Governance scope on repository rows

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Services/GovernanceScope.cs`
- Test: `PanoramicData.NugetManagement.Test/GovernanceScopeTests.cs`

**Interfaces:**
- Consumes: `RepositoryDashboardRow`.
- Produces: `GovernanceScope.ReasonNotGoverned(string?, IReadOnlyList<string>)` unchanged; `Apply(RepositoryDashboardRow row, IReadOnlyList<string> organizations)` replacing the `PackageDashboardRow` overload.

- [ ] **Step 1: Update the tests** — in `GovernanceScopeTests.cs`, change every `new PackageDashboardRow { PackageId = "X", RepositoryFullName = "o/n", ... }` to `new RepositoryDashboardRow { RepositoryFullName = "o/n", Packages = [new() { PackageId = "X" }], ... }`. Keep every assertion exactly as it is.

- [ ] **Step 2: Run to verify they fail**

```
dotnet build PanoramicData.NugetManagement.slnx
```

Expected: FAIL on the `Apply` signature.

- [ ] **Step 3: Change the signature** — in `GovernanceScope.Apply`, change the parameter type to `RepositoryDashboardRow`. The body is unchanged: it reads `RepositoryFullName` and writes `NotGovernedReason`, `IsGoverned` and the clone fields, all of which the new row has.

- [ ] **Step 4: Run to verify they pass**

```
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*GovernanceScopeTests"
```

Expected: `failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Judge governance on the repository row"
```

---

## Task 7: The repository layer in the tree

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Services/NavTreeDataProvider.cs`
- Modify: `PanoramicData.NugetManagement.Web/Models/NavItem.cs:152`
- Test: `PanoramicData.NugetManagement.Test/RepositoryNavTreeTests.cs`

**Interfaces:**
- Consumes: `RepositoryDashboardRow` (Task 3), cache accessors (Task 4).
- Produces, as public statics on `NavTreeDataProvider` beside the existing `OrgKey`/`ReposKey`/`IssuesKey`/`NotGovernedKey`:
  - `RepoKey(string repositoryFullName)` → `repo:{repositoryFullName}`
  - `PackagesKey(string repositoryFullName)` → `pkgs:{repositoryFullName}`
  - `PackageKey(string repositoryFullName, string packageId)` → `pkg:{repositoryFullName}:{packageId}`
  - `CategoryKey(string repositoryFullName, AssessmentCategory category)` → `cat:{repositoryFullName}:{category}`
  - `RuleKey(string repositoryFullName, string ruleId)` → `rule:{repositoryFullName}:{ruleId}`
- `NavView` gains `RepositoryDetail`.

- [ ] **Step 1: Write the failing test**

Create `PanoramicData.NugetManagement.Test/RepositoryNavTreeTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the layer between the estate and its packages. The rules assess a repository, so the
/// categories hang off the repository; the packages it publishes are a branch of their own, and no
/// finding is shown twice.
/// </summary>
public class RepositoryNavTreeTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private const string ECharts = "panoramicdata/PanoramicData.ECharts";

	private readonly string _cacheDirectory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	[Fact]
	public void OneRepositoryShouldAppearOnceHoweverManyPackagesItPublishes()
		=> BuildTree()
			.Where(item => item.ParentKey == NavTreeDataProvider.ReposKey("panoramicdata"))
			.Should().ContainSingle()
			.Which.Text.Should().Be("PanoramicData.ECharts", "the owner is already the node above");

	[Fact]
	public void ThePackagesShouldHangOffTheirOwnBranch()
		=> BuildTree()
			.Where(item => item.ParentKey == NavTreeDataProvider.PackagesKey(ECharts))
			.Select(item => item.PackageId)
			.Should().BeEquivalentTo(
			[
				"PanoramicData.ECharts",
				"PanoramicData.ECharts.BindingGenerator",
				"PanoramicData.ECharts.Samples",
				"PanoramicData.ECharts.Sandbox"
			]);

	[Fact]
	public void ThePackagesBranchShouldCountWhatItHolds()
		=> BuildTree()
			.Single(item => item.Key == NavTreeDataProvider.PackagesKey(ECharts))
			.Text.Should().Be("Packages (4)");

	[Fact]
	public void ThePackagesBranchShouldHangOffTheRepository()
		=> BuildTree()
			.Single(item => item.Key == NavTreeDataProvider.PackagesKey(ECharts))
			.ParentKey.Should().Be(NavTreeDataProvider.RepoKey(ECharts));

	[Fact]
	public void ARepositoryPublishingOnePackageShouldStillHaveTheBranch()
		=> BuildTree(single: true)
			.Should().Contain(item => item.Key == NavTreeDataProvider.PackagesKey("panoramicdata/Meraki.Api"),
				"the shape of the tree must not change under the reader");

	[Fact]
	public void TheRepositoryShouldCarryTheEyeToggle()
		=> BuildTree()
			.Single(item => item.Key == NavTreeDataProvider.RepoKey(ECharts))
			.RepositoryFullName.Should().Be(ECharts, "the toggle renders only where this is set");

	[Fact]
	public void APackageShouldNotCarryTheEyeToggle()
		=> BuildTree()
			.Where(item => item.ParentKey == NavTreeDataProvider.PackagesKey(ECharts))
			.Should().OnlyContain(item => item.RepositoryFullName == null,
				"excluding a repository is not something one of its packages can do");

	[Fact]
	public void APackageOutOfStepWithTheTagShouldSayItsVersion()
		=> BuildTree()
			.Single(item => item.Key == NavTreeDataProvider.PackageKey(ECharts, "PanoramicData.ECharts.Samples"))
			.Text.Should().Contain("1.4.0");

	private List<NavItem> BuildTree(bool single = false)
	{
		var rows = single
			? new List<RepositoryDashboardRow>
			{
				new()
				{
					RepositoryFullName = "panoramicdata/Meraki.Api",
					Organization = "panoramicdata",
					Packages = [new() { PackageId = "Meraki.Api", LatestVersion = "1.0.0" }]
				}
			}
			:
			[
				new()
				{
					RepositoryFullName = ECharts,
					Organization = "panoramicdata",
					LatestTag = "1.4.2",
					Packages =
					[
						new() { PackageId = "PanoramicData.ECharts", LatestVersion = "1.4.2" },
						new() { PackageId = "PanoramicData.ECharts.BindingGenerator", LatestVersion = "1.4.2" },
						new() { PackageId = "PanoramicData.ECharts.Samples", LatestVersion = "1.4.0" },
						new() { PackageId = "PanoramicData.ECharts.Sandbox", LatestVersion = "1.4.2" }
					]
				}
			];

		Directory.CreateDirectory(_cacheDirectory);
		var cache = new DashboardCacheService(
			NullLogger<DashboardCacheService>.Instance,
			Path.Combine(_cacheDirectory, "dashboard-cache.json"));
		cache.SetRows(rows);

		var settings = Options.Create(new AppSettings { NuGetOrganization = "panoramicdata" });

		return new NavTreeDataProvider(
			cache,
			new RuntimeSettingsService(settings, NullLogger<RuntimeSettingsService>.Instance),
			settings).BuildNavItems();
	}

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_cacheDirectory))
			{
				Directory.Delete(_cacheDirectory, recursive: true);
			}
		}
		catch (IOException)
		{
			// A leftover temp directory is not worth failing a test over.
		}

		GC.SuppressFinalize(this);
	}
}
```

- [ ] **Step 2: Run to verify it fails**

```
dotnet build PanoramicData.NugetManagement.slnx
```

Expected: FAIL, `CS0117: 'NavTreeDataProvider' does not contain a definition for 'RepoKey'`.

- [ ] **Step 3: Add the keys and `NavView.RepositoryDetail`**

In `NavItem.cs`, before `PackageDetail`:

```csharp
	/// <summary>Repository-level detail: the assessment, the clone, and the actions that act on it.</summary>
	RepositoryDetail,
```

In `NavTreeDataProvider.cs`, beside the existing key builders:

```csharp
	/// <summary>Builds the key for a repository node.</summary>
	public static string RepoKey(string repositoryFullName) => $"repo:{repositoryFullName}";

	/// <summary>Builds the key for a repository's "Packages" container.</summary>
	public static string PackagesKey(string repositoryFullName) => $"pkgs:{repositoryFullName}";

	/// <summary>Builds the key for one package published by a repository.</summary>
	public static string PackageKey(string repositoryFullName, string packageId)
		=> $"pkg:{repositoryFullName}:{packageId}";

	/// <summary>Builds the key for one assessment category of a repository.</summary>
	public static string CategoryKey(string repositoryFullName, AssessmentCategory category)
		=> $"cat:{repositoryFullName}:{category}";

	/// <summary>Builds the key for one failing rule of a repository.</summary>
	public static string RuleKey(string repositoryFullName, string ruleId)
		=> $"rule:{repositoryFullName}:{ruleId}";
```

- [ ] **Step 4: Rewrite `AddPackageNodes` as `AddRepositoryNodes`**

Replace the method (currently `NavTreeDataProvider.cs:268-365`) with:

```csharp
	/// <summary>
	/// Adds the repository → { packages, category → rule } branch for one organisation.
	/// </summary>
	/// <remarks>
	/// The categories hang off the repository rather than off a package, because that is what the
	/// rules evaluate. While a package stood in for its repository, a repository publishing four
	/// packages reported the same findings four times and was remediated four times over.
	/// </remarks>
	private void AddRepositoryNodes(
		List<NavItem> items,
		string organization,
		string reposKey,
		List<RepositoryDashboardRow>? visibleRows)
	{
		if (visibleRows is null)
		{
			return;
		}

		var guardStates = GuardStatesNeedingAttention();

		foreach (var row in visibleRows.OrderBy(r => r.RepositoryFullName, StringComparer.OrdinalIgnoreCase))
		{
			var repoKey = RepoKey(row.RepositoryFullName);

			items.Add(new NavItem
			{
				Key = repoKey,
				// The owner is already the organisation node above; repeating it in every child would
				// cost the width the repository names need.
				Text = row.RepositoryFullName.Split('/')[^1],
				ParentKey = reposKey,
				IconCss = BuildRepositoryIconCss(row),
				View = NavView.RepositoryDetail,
				Organization = organization,
				IsLeaf = false,
				IssueCount = row.TotalFailures,
				HasErrors = row.TotalCriticals > 0 || row.TotalErrors > 0,
				HasWarnings = row.TotalWarnings > 0,
				IsWorkingTreeDirty = row.IsWorkingTreeClean == false,
				RepositoryFullName = row.RepositoryFullName,
				IsExcluded = _runtimeSettings.IsRepositoryExcluded(row.RepositoryFullName),
				HealthStatus = row.HealthStatus,
				GuardStateNeedingAttention =
					guardStates.TryGetValue(row.RepositoryFullName, out var guardState) ? guardState : null
			});

			var packagesKey = PackagesKey(row.RepositoryFullName);

			items.Add(new NavItem
			{
				Key = packagesKey,
				Text = $"Packages ({row.Packages.Count})",
				ParentKey = repoKey,
				IconCss = "fas fa-box",
				View = NavView.None,
				Organization = organization,
				IsLeaf = row.Packages.Count == 0,
				SortOrder = 0
			});

			foreach (var package in row.Packages)
			{
				items.Add(new NavItem
				{
					Key = PackageKey(row.RepositoryFullName, package.PackageId),
					Text = package.LatestVersion is null
						? package.PackageId
						: $"{package.PackageId}  {package.LatestVersion}",
					ParentKey = packagesKey,
					// Amber where the published version and the repository's tag disagree: the package
					// on nuget.org is not the source the tag points at.
					IconCss = package.MatchesTag(row.LatestTag) == false
						? "fas fa-cube text-warning"
						: "fas fa-cube text-muted",
					View = NavView.PackageDetail,
					Organization = organization,
					PackageId = package.PackageId,
					// Deliberately not set: excluding a repository from governance is not something one
					// of its packages can do, and the eye toggle renders wherever this is.
					IsLeaf = true
				});
			}

			if (row.Assessment is null)
			{
				continue;
			}

			foreach (var category in row.CategorySummaries.Keys.OrderBy(c => c.ToString()))
			{
				var catKey = CategoryKey(row.RepositoryFullName, category);
				var catFailures = row.Assessment.RuleResults
					.Where(r => r.Category == category && !r.Passed)
					.ToList();

				items.Add(new NavItem
				{
					Key = catKey,
					Text = category.ToString(),
					ParentKey = repoKey,
					IconCss = NavHealthRollup.Icon("fas fa-folder", NavHealthRollup.Worst(
						catFailures.Select(r => NavHealthRollup.FromSeverity(r.Severity)))),
					View = NavView.CategoryDetail,
					Organization = organization,
					RepositoryFullName = null,
					PackageId = null,
					Category = category,
					IsLeaf = catFailures.Count == 0,
					// Below Packages, which takes rank 0, so the shape reads the same on every repository.
					SortOrder = 1,
					IssueCount = catFailures.Count
				});

				foreach (var rule in catFailures)
				{
					items.Add(new NavItem
					{
						Key = RuleKey(row.RepositoryFullName, rule.RuleId),
						Text = rule.RuleId,
						ParentKey = catKey,
						IconCss = GetRuleIcon(rule.Severity),
						View = NavView.RuleDetail,
						Organization = organization,
						Category = category,
						RuleId = rule.RuleId,
						IsLeaf = true
					});
				}
			}
		}
	}
```

Keep the existing category/rule body if it differs from the sketch above — the point is the parent
key and the key builders, not a rewrite of how a category is summarised. Read
`NavTreeDataProvider.cs:315-365` first and preserve what it does.

Rename `BuildPackageIconCss` to `BuildRepositoryIconCss` and change its parameter to
`RepositoryDashboardRow`; its body is unchanged. Update its one caller in `Home.razor`'s node
template and the call in `AddRepositoryNodes`.

Finally, rename the call site in `AddOrganizationNodes` from `AddPackageNodes(...)` to
`AddRepositoryNodes(...)`.

- [ ] **Step 5: Run to verify it passes**

```
dotnet build PanoramicData.NugetManagement.slnx
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*RepositoryNavTreeTests"
```

Expected: `total: 8, failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Put the repository between the estate and the packages it publishes"
```

---

## Task 8: The Not governed branch reads the ungoverned list

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Services/NavTreeDataProvider.cs:483-521`
- Test: `PanoramicData.NugetManagement.Test/NotGovernedNavNodeTests.cs`

**Interfaces:**
- Consumes: `DashboardCacheService.GetUngovernedPackages()` (Task 4), `UngovernedPackage` (Task 3).
- Produces: `AddNotGovernedNodes(List<NavItem> items, string organization, string orgKey, IReadOnlyList<UngovernedPackage> packages)`.

- [ ] **Step 1: Update `BuildTree` in the existing tests**

In `NotGovernedNavNodeTests.BuildTree`, the governed row becomes a `RepositoryDashboardRow` and the ungoverned package moves out of the row list:

```csharp
		var rows = new List<RepositoryDashboardRow>
		{
			new()
			{
				RepositoryFullName = "panoramicdata/Meraki.Api",
				Organization = "panoramicdata",
				Packages = [new() { PackageId = "Meraki.Api" }]
			}
		};

		var ungoverned = includeUngoverned
			? new List<UngovernedPackage>
			{
				new()
				{
					PackageId = "Vizor.ECharts.Net80",
					Organization = "panoramicdata",
					DeclaredRepository = "datahint-eu/vizor-echarts",
					Reason = "The nuspec declares datahint-eu/vizor-echarts, which is not one of our organisations."
				}
			}
			: [];
```

then `cache.SetRows(rows); cache.SetUngovernedPackages(ungoverned);`.

`AnUngovernedPackageShouldNotAppearAmongTheRepositories` keeps asserting `["Meraki.Api"]` — the repository is named `Meraki.Api` too, so the assertion holds unchanged. Every other assertion is untouched.

- [ ] **Step 2: Run to verify it fails**

```
dotnet build PanoramicData.NugetManagement.slnx
```

Expected: FAIL on `SetUngovernedPackages` not being reachable from the tree builder yet.

- [ ] **Step 3: Read the ungoverned list**

In `BuildNavItems`/`AddOrganizationNodes`, replace `var ungovernedRows = ApplyFilters(orgRows?.Where(r => !r.IsGoverned).ToList());` with a read of `_cache.GetUngovernedPackages()` filtered to this organisation and, when `FilterRegex` is set, to matching `PackageId`. Change `AddNotGovernedNodes` to take `IReadOnlyList<UngovernedPackage>` and read `package.PackageId` and `package.Reason`. The node text, keys, icons and sort order are unchanged.

- [ ] **Step 4: Run to verify it passes**

```
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*NotGovernedNavNodeTests"
```

Expected: `total: 6, failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Account for ungoverned packages from a list of their own"
```

---

## Task 9: Rollups, issue tree and search

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Services/NavHealthRollup.cs:77`
- Modify: `PanoramicData.NugetManagement.Web/Services/IssueTreeDataProvider.cs`
- Modify: `PanoramicData.NugetManagement.Web/Services/PackageDashboardDataProvider.cs:35`
- Test: `PanoramicData.NugetManagement.Test/NavHealthRollupTests.cs`

**Interfaces:**
- Consumes: `RepositoryDashboardRow`.
- Produces: `NavHealthRollup.ForRepositories(IEnumerable<RepositoryDashboardRow>?)`; `PackageDashboardDataProvider` searching repository name and any package id.

- [ ] **Step 1: Update `NavHealthRollupTests`** — change the row type in every fixture; the rank and worst-of semantics are unchanged, so no assertion changes.

- [ ] **Step 2: Run to verify it fails**

```
dotnet build PanoramicData.NugetManagement.slnx
```

- [ ] **Step 3: Change the three services**

- `NavHealthRollup.ForRepositories` — parameter type only.
- `IssueTreeDataProvider` — the `AddIssueHierarchy` projection currently builds `AssessedPackage(r.RepositoryFullName ?? r.PackageId, ..., r.PackageId)`. With one row per repository the fallback disappears: use `new AssessedPackage(row.RepositoryFullName, row.Assessment, row.RepositoryFullName)`. Affected-repository counts are now genuinely repository counts.
- `PackageDashboardDataProvider` line 35 — replace the `PackageId` search with:

```csharp
				|| r.RepositoryFullName.Contains(search, StringComparison.OrdinalIgnoreCase)
				|| r.Packages.Any(p => p.PackageId.Contains(search, StringComparison.OrdinalIgnoreCase))
```

- [ ] **Step 4: Run to verify it passes**

```
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*NavHealthRollupTests"
```

Expected: `failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Count repositories where repositories were always meant"
```

---

## Task 10: The pages

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Components/Pages/Home.razor`
- Modify: `PanoramicData.NugetManagement.Web/Components/IssuesView.razor`
- Test: `PanoramicData.NugetManagement.Test/GroupedRemediationPromptTests.cs`, `PanoramicData.NugetManagement.Test/GovernanceScopeTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 3 to 9.
- Produces: no new public surface. `_rows` becomes `List<RepositoryDashboardRow>`.

This task is mechanical and large. Work through the compiler errors in order rather than reading the file end to end.

- [ ] **Step 1: Change the field type** — `_rows` and every local, parameter and lambda over it become `RepositoryDashboardRow`.

- [ ] **Step 2: Fix each `row.PackageId` use.** There are three kinds, and they need different answers:
  - *Identity of the thing being acted on* (clone, assess, branch, commit, build, publish) → `row.RepositoryFullName`.
  - *Display of what is published* → `row.Packages`, joined with `", "` where one string is needed.
  - *A NuGet API call about a package* (`IsPackageListedAsync`, version lookups) → iterate `row.Packages`.

- [ ] **Step 3: Fix `row.LatestVersion`** → `row.Packages` versions. Where a single value is wanted for a repository, use the version of the package whose id equals the repository name if there is one, else the first package.

- [ ] **Step 4: Fix `row.NuGetVersionMatchesTag`** → `row.AnyPackageOutOfStepWithTag` (note the inverted sense — the old property was true when matching).

- [ ] **Step 5: Fix the selection handlers** — `OnNavNodeActivated` and `OnNavSelectionChanged` gain a `NavView.RepositoryDetail` case showing what `PackageDetail` used to; `PackageDetail` narrows to the per-package panel. Node lookups keyed on `PackageId` become lookups on `RepositoryFullName`, using `GetRowByPackageId` where only a package id is in hand.

- [ ] **Step 6: Fix the assessment and work-queue loops** — `Home.razor:4068` (`freshRows.Where(r => r.IsGoverned && r.RepositoryFullName is not null)`) drops the null check, since the row cannot exist without a repository. Enqueue by `RepositoryFullName`.

- [ ] **Step 7: Surface unread nuspecs to the user**

The Warning log from Task 2 is not enough on its own: a repository silently kept at yesterday's
mapping looks identical to one confirmed today. After a rediscovery completes, if
`_cache.GetUngovernedPackages()` contains any whose `Reason` starts with `"Could not read the nuspec"`,
show the existing error banner:

```csharp
	var unread = _cache.GetUngovernedPackages()
		.Where(package => package.Reason.StartsWith("Could not read the nuspec", StringComparison.Ordinal))
		.Select(package => package.PackageId)
		.ToList();

	if (unread.Count > 0)
	{
		// Named, not counted. "3 packages failed" sends the reader to the log; the names send them
		// straight to the rediscover button for the ones that matter.
		ShowError($"Could not read the nuspec for {string.Join(", ", unread)}. Rediscover to try again.");
	}
```

Use whichever banner helper `Home.razor` already has — match the surrounding call, do not add a new
mechanism.

- [ ] **Step 8: Update the two remaining test classes** to the new row type, keeping every assertion.

- [ ] **Step 9: Build clean and run the whole suite**

```
Get-Process -Name "PanoramicData.NugetManagement.Web" -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build PanoramicData.NugetManagement.slnx
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe
```

Expected: `Build succeeded`, `0 Error(s)`, and `failed: 1` — only the pre-existing `GitHubAssessment_ThisRepository_ShouldBeCompliant`.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "Act on the repository, throughout the pages that act"
```

---

## Task 11: Verify against the real estate

**Files:** none — this task is evidence, not code.

- [ ] **Step 1: Discard the stale cache**

```powershell
Remove-Item "$env:LOCALAPPDATA\PanoramicData.NugetManagement\dashboard-cache.json" -ErrorAction SilentlyContinue
```

(The version bump would discard it anyway; removing it makes the run unambiguous.)

- [ ] **Step 2: Run the app and rediscover**

```
dotnet run --project PanoramicData.NugetManagement.Web
```

Open `http://localhost:5023` and rediscover `panoramicdata`.

- [ ] **Step 3: Confirm each claim of the spec**

- `ConnectWise.Manage.Api` appears under **Repositories** as `ConnectWise.Manage.Api`, not under Not governed.
- `PanoramicData.ECharts` appears **once**, with `Packages (4)`.
- `Not governed` holds **7** entries, not 15, and none of them says "declares no repository" about a package that declares one.
- The repository count is roughly 98, down from 106 package rows.
- No console warning about unread nuspecs on a clean run.

- [ ] **Step 4: Record the outcome**

Append a short "Verified" section to the spec stating the observed repository count, the Not-governed count, and the date. Commit it.

```bash
git add docs/superpowers/specs/2026-08-29-repository-tree-layer-design.md
git commit -m "Record what the repository layer produced against the real estate"
```

---

## Task 12: Merge to main

- [ ] **Step 1: Rebase onto whatever main has become**

`main` is under active concurrent development, including in `NuGetDiscoveryService`.

```bash
git fetch origin
git rebase main
```

Resolve conflicts in favour of keeping both changes; re-run the suite after.

- [ ] **Step 2: Re-run the full suite** — same command as Task 10, Step 8. Same expectation.

- [ ] **Step 3: Merge**

```bash
git switch main
git merge --no-ff worktree-repo-tree-layer
```

- [ ] **Step 4: Report** the final test count and what changed, and leave pushing to David.
