# NuGet Version Floor and Upstream Cache Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop PKG-05/06/07 asking nuget.org "are you on the newest version in the world?" on every run, and instead gate on an estate-learned floor plus a published-date grace period read from a committed cache.

**Architecture:** Two committed JSON stores at the scanner repository root — `nuget-versions.json` (an upstream snapshot, written only by a rate-limited refresher) and `nuget-floors.json` (a ratcheting floor learned from the estate's own repositories). Rules read both and never touch the network. Both are reached through settable static `Default` singletons, because `RuleRegistry` builds rules via `Activator.CreateInstance` and cannot inject them.

**Tech Stack:** .NET 10, xUnit v3, AwesomeAssertions, NuGet.Protocol 7.9.0, `System.Text.Json`, `TimeProvider`.

**Spec:** `docs/superpowers/specs/2026-08-30-nuget-version-floor-and-cache-design.md`

## Global Constraints

- Target framework `net10.0`; tabs for indentation; file-scoped namespaces; XML docs on all public members.
- Tests use `TestWithOutput` and AwesomeAssertions (`.Should()`). FluentAssertions is banned by TST-08.
- **`dotnet test` reports "Zero tests ran" in this repo and exits 5.** Always run the binary directly:
  `./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*ClassName"`
- **Stop the dev server before every build** or the build silently fails and tests run against a stale binary:
  `Get-Process -Name "PanoramicData.NugetManagement.Web" -ErrorAction SilentlyContinue | Stop-Process -Force`
- Grace periods, copied verbatim from the spec: **build/patch 30 days, minor 90 days, major 365 days.**
- Store file names: **`nuget-versions.json`**, **`nuget-floors.json`**, both at the scanner repository root (beside `PanoramicData.NugetManagement.slnx`), both committed.
- The test project already has `InternalsVisibleTo`, so `internal` members are reachable from tests.
- Commit messages end with: `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`

---

## File Structure

**New — library (`PanoramicData.NugetManagement/Services/`)**

| File | Responsibility |
|---|---|
| `NuGetVersionCache.cs` | `NuGetVersionSnapshot` record; load/read/update/persist the upstream snapshot; `Default` singleton. |
| `NuGetFloorCatalog.cs` | `NuGetFloorBump` record; frozen floor for gating, `Observe` ratchet, persistence, `RecentBumps`; `Default` singleton. |

**New — web (`PanoramicData.NugetManagement.Web/Services/`)**

| File | Responsibility |
|---|---|
| `NuGetVersionRefresher.cs` | The only component that contacts nuget.org for dependency versions. Rate-limited sweep; callable once or hosted. |

**New — tests (`PanoramicData.NugetManagement.Test/`)**

`NuGetVersionCacheTests.cs`, `NuGetFloorCatalogTests.cs`, `NuGetPackageUpdateGateTests.cs`, `NuGetVersionRefresherTests.cs`

**Modified**

| File | Change |
|---|---|
| `Services/NuGetVersionChecker.cs` | Return the published date alongside the version. |
| `Rules/NuGetHygiene/NuGetPackageUpdateRuleBase.cs` | Read cache + floor + clock instead of resolving live; add `GraceDays`. |
| `Rules/NuGetHygiene/NuGetBuildLevelUpdatesRule.cs` etc. (×3) | Declare `GraceDays`. |
| `Program.cs` | Register the refresher. |
| `Test/SelfAssessmentTests.cs`, `Test/GitHubIntegrationTests.cs` | Stop asserting grace-dependent verdicts. |

---

### Task 1: Upstream version cache — reading

**Files:**
- Create: `PanoramicData.NugetManagement/Services/NuGetVersionCache.cs`
- Test: `PanoramicData.NugetManagement.Test/NuGetVersionCacheTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `NuGetVersionSnapshot(string PackageId, string LatestVersion, DateTimeOffset Published, DateTimeOffset RefreshedAtUtc)`; `NuGetVersionCache(string? filePath)`, `bool TryGet(string, out NuGetVersionSnapshot)`, `const string FileName`, `static NuGetVersionCache Default { get; set; }`.

- [ ] **Step 1: Write the failing test**

Create `PanoramicData.NugetManagement.Test/NuGetVersionCacheTests.cs`:

```csharp
using System.Text.Json;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests the committed snapshot of what nuget.org last reported. A missing or corrupt file must
/// leave every package "unknown" rather than invent a version: a guessed answer here becomes a
/// governance verdict against a repository.
/// </summary>
public class NuGetVersionCacheTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _directory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	[Fact]
	public void ShouldReadASnapshotThatWasWrittenToDisk()
	{
		WriteFile("""
			{
			  "Codacy.Api": {
			    "latestVersion": "3.0.43",
			    "published": "2026-08-12T00:00:00+00:00",
			    "refreshedAtUtc": "2026-08-29T00:00:00+00:00"
			  }
			}
			""");

		new NuGetVersionCache(Path.Combine(_directory, NuGetVersionCache.FileName))
			.TryGet("Codacy.Api", out var snapshot).Should().BeTrue();

		snapshot.LatestVersion.Should().Be("3.0.43");
		snapshot.Published.Should().Be(new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero));
	}

	[Fact]
	public void ShouldNotBeCaseSensitiveAboutPackageIds()
	{
		WriteFile("""
			{ "Codacy.Api": { "latestVersion": "3.0.43", "published": "2026-08-12T00:00:00+00:00", "refreshedAtUtc": "2026-08-29T00:00:00+00:00" } }
			""");

		new NuGetVersionCache(Path.Combine(_directory, NuGetVersionCache.FileName))
			.TryGet("codacy.api", out _).Should().BeTrue();
	}

	[Fact]
	public void AnAbsentFileShouldLeaveEveryPackageUnknown()
		=> new NuGetVersionCache(Path.Combine(_directory, NuGetVersionCache.FileName))
			.TryGet("Codacy.Api", out _).Should().BeFalse();

	[Fact]
	public void ACorruptFileShouldLeaveEveryPackageUnknownRatherThanThrow()
	{
		WriteFile("{ this is not json");

		new NuGetVersionCache(Path.Combine(_directory, NuGetVersionCache.FileName))
			.TryGet("Codacy.Api", out _).Should().BeFalse();
	}

	[Fact]
	public void ANullPathShouldBeAnEmptyInMemoryCache()
		=> new NuGetVersionCache(null).TryGet("Codacy.Api", out _).Should().BeFalse();

	private void WriteFile(string json)
	{
		Directory.CreateDirectory(_directory);
		File.WriteAllText(Path.Combine(_directory, NuGetVersionCache.FileName), json);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_directory))
			{
				Directory.Delete(_directory, recursive: true);
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

- [ ] **Step 2: Run the test to verify it fails**

```bash
Get-Process -Name "PanoramicData.NugetManagement.Web" -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj
```

Expected: FAIL to compile — `The type or namespace name 'NuGetVersionCache' could not be found`.

- [ ] **Step 3: Write the minimal implementation**

Create `PanoramicData.NugetManagement/Services/NuGetVersionCache.cs`:

```csharp
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// What nuget.org last reported about one package.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="LatestVersion">The latest stable version published.</param>
/// <param name="Published">When that version was published, per nuget.org.</param>
/// <param name="RefreshedAtUtc">When this entry last changed.</param>
public sealed record NuGetVersionSnapshot(
	string PackageId,
	string LatestVersion,
	DateTimeOffset Published,
	DateTimeOffset RefreshedAtUtc);

/// <summary>
/// A committed snapshot of the latest stable version of each package the estate depends on.
/// </summary>
/// <remarks>
/// <para>
/// The rules read this and never contact nuget.org, so an assessment is a pure function of the
/// repository plus this file: reproducible, offline, and moving only when a refresh is committed.
/// Resolving "latest" live meant a repository that changed nothing turned red because a stranger
/// published a patch.
/// </para>
/// <para>
/// A miss is "unknown", never a guess. An absent or corrupt file therefore disables the upstream
/// half of the gate entirely rather than inventing versions to judge repositories against.
/// </para>
/// </remarks>
public sealed class NuGetVersionCache
{
	/// <summary>The file this cache is persisted to, at the scanner repository root.</summary>
	public const string FileName = "nuget-versions.json";

	private readonly string? _filePath;
	private readonly ConcurrentDictionary<string, NuGetVersionSnapshot> _snapshots;

	private static readonly JsonSerializerOptions _jsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	/// <summary>
	/// Initializes a new instance, loading any persisted snapshot.
	/// </summary>
	/// <param name="filePath">The JSON file path, or null to operate in memory only.</param>
	public NuGetVersionCache(string? filePath)
	{
		_filePath = filePath;
		_snapshots = new(Load(filePath), StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>
	/// The snapshot for a package, if one has been recorded.
	/// </summary>
	/// <param name="packageId">The package identifier.</param>
	/// <param name="snapshot">The snapshot, when found.</param>
	public bool TryGet(string packageId, out NuGetVersionSnapshot snapshot)
		=> _snapshots.TryGetValue(packageId, out snapshot!);

	/// <summary>An entry as stored on disk, keyed by package id.</summary>
	private sealed record Entry(
		[property: JsonPropertyName("latestVersion")] string LatestVersion,
		[property: JsonPropertyName("published")] DateTimeOffset Published,
		[property: JsonPropertyName("refreshedAtUtc")] DateTimeOffset RefreshedAtUtc);

	private static Dictionary<string, NuGetVersionSnapshot> Load(string? filePath)
	{
		var result = new Dictionary<string, NuGetVersionSnapshot>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
		{
			return result;
		}

		try
		{
			var parsed = JsonSerializer.Deserialize<Dictionary<string, Entry>>(
				File.ReadAllText(filePath),
				_jsonOptions);

			if (parsed is not null)
			{
				foreach (var (packageId, entry) in parsed)
				{
					result[packageId] = new NuGetVersionSnapshot(
						packageId,
						entry.LatestVersion,
						entry.Published,
						entry.RefreshedAtUtc);
				}
			}
		}
		catch
		{
			// Corrupt or unreadable: every package stays unknown, which disables the upstream half of
			// the gate rather than judging repositories against invented versions.
		}

		return result;
	}

	// ── Process-wide default instance used by the rules ──

	private static NuGetVersionCache? _default;

	/// <summary>
	/// The shared cache used during assessment. Assignable so tests can substitute an in-memory
	/// instance (constructed with a null path) that never reads or writes the committed file.
	/// </summary>
	public static NuGetVersionCache Default
	{
		get => _default ??= new NuGetVersionCache(RepositoryRootFile.Resolve(FileName));
		set => _default = value;
	}
}
```

Create `PanoramicData.NugetManagement/Services/RepositoryRootFile.cs` (shared by both stores, so it is written once here):

```csharp
namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Resolves a file that lives at the scanner repository root, beside the solution.
/// </summary>
/// <remarks>
/// Both committed stores need the same answer, and both need it before the file exists so it can be
/// created on first write. Walking up from the running assembly is how
/// <see cref="ActionVersionCatalog"/> already finds <c>action-versions.json</c>.
/// </remarks>
internal static class RepositoryRootFile
{
	/// <summary>
	/// The path a root-level file should have, whether or not it exists yet, or null when the
	/// repository root cannot be found.
	/// </summary>
	/// <param name="fileName">The file name to resolve.</param>
	public static string? Resolve(string fileName)
	{
		var directory = AppContext.BaseDirectory;
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory, "PanoramicData.NugetManagement.slnx")))
			{
				return Path.Combine(directory, fileName);
			}

			directory = Directory.GetParent(directory)?.FullName;
		}

		return null;
	}
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*NuGetVersionCacheTests"
```

Expected: `total: 5, failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add PanoramicData.NugetManagement/Services/NuGetVersionCache.cs PanoramicData.NugetManagement/Services/RepositoryRootFile.cs PanoramicData.NugetManagement.Test/NuGetVersionCacheTests.cs
git commit -m "Read a committed snapshot of what nuget.org last published

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Upstream version cache — writing, only on change

**Files:**
- Modify: `PanoramicData.NugetManagement/Services/NuGetVersionCache.cs`
- Test: `PanoramicData.NugetManagement.Test/NuGetVersionCacheTests.cs`

**Interfaces:**
- Consumes: `NuGetVersionCache`, `NuGetVersionSnapshot` from Task 1.
- Produces: `bool Update(string packageId, string latestVersion, DateTimeOffset published, DateTimeOffset now)` returning true when the stored version changed; `void Persist()`.

- [ ] **Step 1: Write the failing test**

Append these to `NuGetVersionCacheTests.cs`, before `WriteFile`:

```csharp
	[Fact]
	public void ANewVersionShouldBeReportedAsAChangeAndSurviveARestart()
	{
		var path = Path.Combine(_directory, NuGetVersionCache.FileName);
		Directory.CreateDirectory(_directory);

		var cache = new NuGetVersionCache(path);
		cache.Update("Codacy.Api", "3.0.43", Published, Now).Should().BeTrue();
		cache.Persist();

		new NuGetVersionCache(path).TryGet("Codacy.Api", out var reloaded).Should().BeTrue();
		reloaded.LatestVersion.Should().Be("3.0.43");
	}

	[Fact]
	public void RefreshingAnUnchangedVersionShouldNotCountAsAChange()
	{
		var cache = new NuGetVersionCache(null);
		cache.Update("Codacy.Api", "3.0.43", Published, Now).Should().BeTrue();

		cache.Update("Codacy.Api", "3.0.43", Published, Now.AddDays(1))
			.Should().BeFalse("the cache is committed, so a timestamp alone must not dirty the file");
	}

	[Fact]
	public void RefreshingAnUnchangedVersionShouldNotMoveItsRefreshedAt()
	{
		var cache = new NuGetVersionCache(null);
		cache.Update("Codacy.Api", "3.0.43", Published, Now);
		cache.Update("Codacy.Api", "3.0.43", Published, Now.AddDays(1));

		cache.TryGet("Codacy.Api", out var snapshot);
		snapshot.RefreshedAtUtc.Should().Be(Now, "refreshedAtUtc records what changed, not when we looked");
	}

	private static readonly DateTimeOffset Published = new(2026, 8, 12, 0, 0, 0, TimeSpan.Zero);
	private static readonly DateTimeOffset Now = new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj
```

Expected: FAIL to compile — `'NuGetVersionCache' does not contain a definition for 'Update'`.

- [ ] **Step 3: Write the minimal implementation**

Add to `NuGetVersionCache`, after `TryGet`:

```csharp
	/// <summary>
	/// Records what nuget.org reported for a package, and says whether that changed anything.
	/// </summary>
	/// <remarks>
	/// Returns false when the version is the one already held, and leaves
	/// <see cref="NuGetVersionSnapshot.RefreshedAtUtc"/> untouched in that case. The file is committed,
	/// so a sweep that stamped a new timestamp on every package each interval would leave the working
	/// tree permanently modified and bury real version changes in noise.
	/// </remarks>
	/// <param name="packageId">The package identifier.</param>
	/// <param name="latestVersion">The latest stable version nuget.org reports.</param>
	/// <param name="published">When that version was published.</param>
	/// <param name="now">The current time, for stamping a genuine change.</param>
	/// <returns>True when the stored version changed.</returns>
	public bool Update(string packageId, string latestVersion, DateTimeOffset published, DateTimeOffset now)
	{
		if (_snapshots.TryGetValue(packageId, out var existing)
			&& string.Equals(existing.LatestVersion, latestVersion, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		_snapshots[packageId] = new NuGetVersionSnapshot(packageId, latestVersion, published, now);
		return true;
	}

	/// <summary>
	/// Writes the cache to its file. Best-effort: a read-only environment simply keeps what it has.
	/// </summary>
	public void Persist()
	{
		if (string.IsNullOrEmpty(_filePath))
		{
			return;
		}

		try
		{
			var ordered = _snapshots
				.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(
					kvp => kvp.Key,
					kvp => new Entry(kvp.Value.LatestVersion, kvp.Value.Published, kvp.Value.RefreshedAtUtc));

			File.WriteAllText(_filePath, JsonSerializer.Serialize(ordered, _jsonOptions));
		}
		catch
		{
			// Read-only environment (for example a deployed server): persistence is best-effort.
		}
	}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*NuGetVersionCacheTests"
```

Expected: `total: 8, failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add PanoramicData.NugetManagement/Services/NuGetVersionCache.cs PanoramicData.NugetManagement.Test/NuGetVersionCacheTests.cs
git commit -m "Persist the version cache only when a version actually changed

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Estate-learned floor catalog

**Files:**
- Create: `PanoramicData.NugetManagement/Services/NuGetFloorCatalog.cs`
- Test: `PanoramicData.NugetManagement.Test/NuGetFloorCatalogTests.cs`

**Interfaces:**
- Consumes: `RepositoryRootFile.Resolve` from Task 1.
- Produces: `NuGetFloorBump(string PackageId, string From, string To, string? Repository)`; `NuGetFloorCatalog(string? filePath)`, `string? GetFloor(string packageId)`, `void Observe(string packageId, string version, string? repository = null)`, `IReadOnlyList<NuGetFloorBump> RecentBumps`, `const string FileName`, `static NuGetFloorCatalog Default { get; set; }`.

- [ ] **Step 1: Write the failing test**

Create `PanoramicData.NugetManagement.Test/NuGetFloorCatalogTests.cs`:

```csharp
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests the floor learned from the estate's own repositories: the highest version of a package any
/// repository has been seen to declare. A repository below it is behind something we have already
/// proven works, which is a fact about us rather than about nuget.org.
/// </summary>
public class NuGetFloorCatalogTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _directory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	[Fact]
	public void AnUnseenPackageShouldHaveNoFloor()
		=> new NuGetFloorCatalog(null).GetFloor("Codacy.Api").Should().BeNull();

	[Fact]
	public void TheFirstObservationShouldNotRaiseTheFloorWithinTheSameRun()
	{
		// The floor used for pass/fail is frozen at load, so nothing observed during a run can change
		// that run's verdicts. Learning applies to the next run.
		var catalog = new NuGetFloorCatalog(null);

		catalog.Observe("Codacy.Api", "3.0.43");

		catalog.GetFloor("Codacy.Api").Should().BeNull("this run's floor was fixed when the file loaded");
	}

	[Fact]
	public void AHigherVersionShouldRaiseThePersistedFloor()
	{
		var path = Path.Combine(_directory, NuGetFloorCatalog.FileName);
		Directory.CreateDirectory(_directory);

		var catalog = new NuGetFloorCatalog(path);
		catalog.Observe("Codacy.Api", "3.0.43", "panoramicdata/Meraki.Api");

		new NuGetFloorCatalog(path).GetFloor("Codacy.Api").Should().Be("3.0.43");
	}

	[Fact]
	public void ALowerVersionShouldNeverLowerTheFloor()
	{
		var path = Path.Combine(_directory, NuGetFloorCatalog.FileName);
		Directory.CreateDirectory(_directory);

		new NuGetFloorCatalog(path).Observe("Codacy.Api", "3.0.43");

		var second = new NuGetFloorCatalog(path);
		second.Observe("Codacy.Api", "3.0.11");

		new NuGetFloorCatalog(path).GetFloor("Codacy.Api").Should().Be("3.0.43", "the floor is a ratchet");
	}

	[Fact]
	public void RaisingTheFloorShouldRecordWhichRepositoryDidIt()
	{
		var catalog = new NuGetFloorCatalog(null);
		catalog.Observe("Codacy.Api", "3.0.43", "panoramicdata/Meraki.Api");

		var bump = catalog.RecentBumps.Should().ContainSingle().Subject;
		bump.PackageId.Should().Be("Codacy.Api");
		bump.To.Should().Be("3.0.43");
		bump.Repository.Should().Be("panoramicdata/Meraki.Api");
	}

	[Fact]
	public void AnUnparseableVersionShouldBeIgnored()
	{
		var catalog = new NuGetFloorCatalog(null);

		catalog.Observe("Codacy.Api", "$(SomeMsBuildProperty)");

		catalog.RecentBumps.Should().BeEmpty();
	}

	[Fact]
	public void ACorruptFileShouldLeaveEveryFloorUnsetRatherThanThrow()
	{
		Directory.CreateDirectory(_directory);
		var path = Path.Combine(_directory, NuGetFloorCatalog.FileName);
		File.WriteAllText(path, "{ not json");

		new NuGetFloorCatalog(path).GetFloor("Codacy.Api").Should().BeNull();
	}

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_directory))
			{
				Directory.Delete(_directory, recursive: true);
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

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj
```

Expected: FAIL to compile — `The type or namespace name 'NuGetFloorCatalog' could not be found`.

- [ ] **Step 3: Write the minimal implementation**

Create `PanoramicData.NugetManagement/Services/NuGetFloorCatalog.cs`:

```csharp
using System.Collections.Concurrent;
using System.Text.Json;
using NuGet.Versioning;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// A record of a learned floor change for a NuGet package.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="From">The previous floor, or null when there was none.</param>
/// <param name="To">The newly-learned floor.</param>
/// <param name="Repository">The repository whose declaration triggered the learning.</param>
public sealed record NuGetFloorBump(string PackageId, string? From, string To, string? Repository);

/// <summary>
/// A self-updating store of the minimum-acceptable ("floor") version for each NuGet package.
/// </summary>
/// <remarks>
/// <para>
/// The floor is learned from the versions the organization's own repositories actually declare:
/// when a repository is observed on a higher version than the current floor, that becomes the new
/// floor and is persisted to <c>nuget-floors.json</c>. A single repository ahead of the pack is
/// enough — it is the canary, and it has proven the version works.
/// </para>
/// <para>
/// This asks a different question from nuget.org, which only reports what exists and has no opinion
/// on what we should be on. Gating on "newest in the world" made every repository fail whenever a
/// stranger published; gating on the estate's own best asks for consistency, which is achievable.
/// </para>
/// <para>
/// The floor used for pass/fail within a run is frozen at load time, so verdicts cannot shift
/// underneath a run in progress; observations raise the persisted floor for subsequent runs.
/// </para>
/// </remarks>
public sealed class NuGetFloorCatalog
{
	/// <summary>The file this catalogue is persisted to, at the scanner repository root.</summary>
	public const string FileName = "nuget-floors.json";

	private readonly string? _filePath;
	private readonly Dictionary<string, NuGetVersion> _frozenBaseline;
	private readonly ConcurrentDictionary<string, NuGetVersion> _learned;
	private readonly ConcurrentQueue<NuGetFloorBump> _bumps = new();
	private readonly Lock _persistLock = new();

	private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

	/// <summary>
	/// Initializes a new instance, loading any persisted floors.
	/// </summary>
	/// <param name="filePath">The JSON file path, or null to operate in memory only.</param>
	public NuGetFloorCatalog(string? filePath)
	{
		_filePath = filePath;
		var loaded = Load(filePath);
		_frozenBaseline = new(loaded, StringComparer.OrdinalIgnoreCase);
		_learned = new(loaded, StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>The floor changes learned during this process, most recent last.</summary>
	public IReadOnlyList<NuGetFloorBump> RecentBumps => [.. _bumps];

	/// <summary>
	/// The floor for a package, or null when no repository has been seen using it. Stable for the
	/// lifetime of the process.
	/// </summary>
	/// <param name="packageId">The package identifier.</param>
	public string? GetFloor(string packageId)
		=> _frozenBaseline.TryGetValue(packageId, out var version) ? version.ToNormalizedString() : null;

	/// <summary>
	/// Records the version a package was observed at. If it exceeds the current floor, the floor is
	/// raised and persisted, and a bump is recorded for surfacing in the UI.
	/// </summary>
	/// <param name="packageId">The package identifier.</param>
	/// <param name="version">The version the repository declares.</param>
	/// <param name="repository">The repository that declares it.</param>
	public void Observe(string packageId, string version, string? repository = null)
	{
		// Versions can be MSBuild properties rather than literals; those say nothing about the estate.
		if (!NuGetVersion.TryParse(version, out var observed))
		{
			return;
		}

		lock (_persistLock)
		{
			var current = _learned.TryGetValue(packageId, out var existing) ? existing : null;
			if (current is not null && observed <= current)
			{
				return;
			}

			_learned[packageId] = observed;
			_bumps.Enqueue(new NuGetFloorBump(
				packageId,
				current?.ToNormalizedString(),
				observed.ToNormalizedString(),
				repository));

			Persist();
		}
	}

	private void Persist()
	{
		if (string.IsNullOrEmpty(_filePath))
		{
			return;
		}

		try
		{
			var ordered = _learned
				.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToNormalizedString());

			File.WriteAllText(_filePath, JsonSerializer.Serialize(ordered, _jsonOptions));
		}
		catch
		{
			// Read-only environment (for example a deployed server): learning is best-effort. A floor
			// learned in CI therefore evaporates, and only machines that commit move the bar.
		}
	}

	private static Dictionary<string, NuGetVersion> Load(string? filePath)
	{
		var result = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
		{
			return result;
		}

		try
		{
			var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(filePath));
			if (parsed is not null)
			{
				foreach (var (packageId, version) in parsed)
				{
					if (NuGetVersion.TryParse(version, out var floor))
					{
						result[packageId] = floor;
					}
				}
			}
		}
		catch
		{
			// Corrupt or unreadable: no floors, so the consistency half of the gate stands down.
		}

		return result;
	}

	// ── Process-wide default instance used by the rules ──

	private static NuGetFloorCatalog? _default;

	/// <summary>
	/// The shared catalog used during assessment. Assignable so tests can substitute an in-memory
	/// instance (constructed with a null path) that never writes to the committed file.
	/// </summary>
	public static NuGetFloorCatalog Default
	{
		get => _default ??= new NuGetFloorCatalog(RepositoryRootFile.Resolve(FileName));
		set => _default = value;
	}
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*NuGetFloorCatalogTests"
```

Expected: `total: 7, failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add PanoramicData.NugetManagement/Services/NuGetFloorCatalog.cs PanoramicData.NugetManagement.Test/NuGetFloorCatalogTests.cs
git commit -m "Learn a version floor from the estate rather than from nuget.org

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Published date from nuget.org

**Files:**
- Modify: `PanoramicData.NugetManagement/Services/NuGetVersionChecker.cs:81-111`
- Test: none (this is a thin wrapper over the network; it is exercised through the refresher in Task 6).

**Interfaces:**
- Consumes: nothing.
- Produces: `Task<(string Version, DateTimeOffset Published)?> GetLatestStableWithPublishedAsync(string packageId, CancellationToken cancellationToken)`.

- [ ] **Step 1: Write the implementation**

Add to `NuGetVersionChecker`, after `GetLatestStableVersionAsync`:

```csharp
	/// <summary>
	/// Gets the latest stable version of a package together with the date it was published.
	/// </summary>
	/// <remarks>
	/// The published date is what the freshness grace period is measured from, so that "you are 89
	/// days behind a release" is a fact about the release rather than about when this tool first
	/// happened to look. That keeps the verdict identical on every machine, and unaffected by a
	/// wiped or freshly cloned cache.
	/// </remarks>
	/// <param name="packageId">The NuGet package ID.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The version and its publication date, or null when neither can be read.</returns>
	public async Task<(string Version, DateTimeOffset Published)?> GetLatestStableWithPublishedAsync(
		string packageId,
		CancellationToken cancellationToken = default)
	{
		try
		{
			var metadataResource = await _sourceRepository
				.GetResourceAsync<PackageMetadataResource>(cancellationToken)
				.ConfigureAwait(false);

			if (metadataResource is null)
			{
				_logger.LogWarning("NuGet source does not provide package metadata; cannot check {PackageId}", packageId);
				return null;
			}

			var metadata = await metadataResource.GetMetadataAsync(
				packageId,
				includePrerelease: false,
				includeUnlisted: false,
				new SourceCacheContext(),
				NuGet.Common.NullLogger.Instance,
				cancellationToken).ConfigureAwait(false);

			var latest = metadata
				.OrderByDescending(m => m.Identity.Version)
				.FirstOrDefault();

			// A package with no published date cannot be graced, so it is treated as unknown rather
			// than defaulted to "published today" (which would grant a fresh grace period forever) or
			// to the epoch (which would fail every repository immediately).
			return latest?.Published is { } published
				? (latest.Identity.Version.ToNormalizedString(), published)
				: null;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to query NuGet for package {PackageId}", packageId);
			return null;
		}
	}
```

- [ ] **Step 2: Build to verify it compiles**

```bash
Get-Process -Name "PanoramicData.NugetManagement.Web" -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build PanoramicData.NugetManagement/PanoramicData.NugetManagement.csproj
```

Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add PanoramicData.NugetManagement/Services/NuGetVersionChecker.cs
git commit -m "Read a package's publication date, not just its version

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Rewire the three freshness rules

**Files:**
- Modify: `PanoramicData.NugetManagement/Rules/NuGetHygiene/NuGetPackageUpdateRuleBase.cs` (whole file)
- Modify: `NuGetBuildLevelUpdatesRule.cs`, `NuGetMinorLevelUpdatesRule.cs`, `NuGetMajorLevelUpdatesRule.cs`
- Test: `PanoramicData.NugetManagement.Test/NuGetPackageUpdateGateTests.cs`

**Interfaces:**
- Consumes: `NuGetVersionCache`, `NuGetFloorCatalog` (Tasks 1–3); `NuGetVersionChecker.ClassifyUpdateLevel` (existing `internal static`).
- Produces: `protected abstract int GraceDays { get; }`; constructor `NuGetPackageUpdateRuleBase(NuGetVersionCache, NuGetFloorCatalog, TimeProvider)`.

**Which rule reports a below-floor failure:** the one whose `TargetUpdateLevel` equals the semantic gap between the declared version and the floor. Without this, a repository three majors behind would fail all three rules with the same finding.

- [ ] **Step 1: Write the failing test**

Create `PanoramicData.NugetManagement.Test/NuGetPackageUpdateGateTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests the two questions the freshness rules now ask: are you behind the estate (immediate), and
/// have you been behind a published release for longer than its grace period.
/// </summary>
public class NuGetPackageUpdateGateTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly DateTimeOffset _published = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task ShouldFailWhenBehindTheEstateFloorEvenWithNoUpstreamKnowledge()
	{
		var floors = new NuGetFloorCatalog(null);
		floors.Observe("Codacy.Api", "3.0.43");

		// A second catalog loads nothing, so freeze the floor by persisting through a real file is
		// unnecessary here: construct the rule with a catalog whose baseline already holds the floor.
		var result = await Evaluate(
			declaredVersion: "3.0.11",
			cache: new NuGetVersionCache(null),
			floors: FrozenFloor("Codacy.Api", "3.0.43"),
			now: _published.AddDays(1));

		result.Passed.Should().BeFalse("the estate has already proven 3.0.43 works");
		result.Message.Should().Contain("3.0.43");
	}

	[Fact]
	public async Task ShouldPassWhenBehindUpstreamButInsideTheGracePeriod()
	{
		var result = await Evaluate(
			declaredVersion: "3.0.42",
			cache: CacheWith("Codacy.Api", "3.0.43", _published),
			floors: new NuGetFloorCatalog(null),
			now: _published.AddDays(29));

		result.Passed.Should().BeTrue("30 days is the build-level grace");
	}

	[Fact]
	public async Task ShouldFailWhenBehindUpstreamForLongerThanTheGracePeriod()
	{
		var result = await Evaluate(
			declaredVersion: "3.0.42",
			cache: CacheWith("Codacy.Api", "3.0.43", _published),
			floors: new NuGetFloorCatalog(null),
			now: _published.AddDays(31));

		result.Passed.Should().BeFalse();
	}

	[Fact]
	public async Task ShouldPassWhenUpstreamIsUnknown()
	{
		var result = await Evaluate(
			declaredVersion: "3.0.42",
			cache: new NuGetVersionCache(null),
			floors: new NuGetFloorCatalog(null),
			now: _published.AddYears(5));

		result.Passed.Should().BeTrue("an empty cache means unknown, and unknown is never a failure");
	}

	[Fact]
	public async Task ShouldPassWhenAheadOfUpstream()
	{
		var result = await Evaluate(
			declaredVersion: "3.0.44",
			cache: CacheWith("Codacy.Api", "3.0.43", _published),
			floors: new NuGetFloorCatalog(null),
			now: _published.AddYears(5));

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task ShouldIgnoreAGapThatBelongsToAnotherRule()
	{
		// A major gap is PKG-07's to report, not PKG-05's.
		var result = await Evaluate(
			declaredVersion: "2.0.0",
			cache: CacheWith("Codacy.Api", "3.0.43", _published),
			floors: new NuGetFloorCatalog(null),
			now: _published.AddYears(5));

		result.Passed.Should().BeTrue("PKG-05 reports build-level gaps only");
	}

	private static NuGetVersionCache CacheWith(string packageId, string version, DateTimeOffset published)
	{
		var cache = new NuGetVersionCache(null);
		cache.Update(packageId, version, published, published);
		return cache;
	}

	/// <summary>A catalog whose frozen baseline already holds a floor, as it would on a second run.</summary>
	private static NuGetFloorCatalog FrozenFloor(string packageId, string version)
	{
		var path = Path.Combine(Path.GetTempPath(), "nugetmanagement-tests", Guid.NewGuid().ToString("n"));
		Directory.CreateDirectory(path);
		var file = Path.Combine(path, NuGetFloorCatalog.FileName);

		new NuGetFloorCatalog(file).Observe(packageId, version);
		return new NuGetFloorCatalog(file);
	}

	private static async Task<RuleResult> Evaluate(
		string declaredVersion,
		NuGetVersionCache cache,
		NuGetFloorCatalog floors,
		DateTimeOffset now)
	{
		var timeProvider = new FakeTimeProvider(now);
		var rule = new NuGetBuildLevelUpdatesRule(cache, floors, timeProvider);

		var context = new RepositoryContext
		{
			FullName = "panoramicdata/Sample.Api",
			Name = "Sample.Api",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = ["Directory.Packages.props"],
			FileContents = new Dictionary<string, string>
			{
				["Directory.Packages.props"] = $"""
					<Project>
					  <ItemGroup>
					    <PackageVersion Include="Codacy.Api" Version="{declaredVersion}" />
					  </ItemGroup>
					</Project>
					"""
			}
		};

		return await rule.EvaluateAsync(context, TestContext.Current.CancellationToken);
	}
}
```

Add the fake clock package to `PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj`:

```xml
		<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
```

and to `Directory.Packages.props`:

```xml
    <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.9.0" />
```

10.9.0 was the latest stable on 2026-08-30. Check for a newer one before pinning — this repository's own PKG-05 assesses `Directory.Packages.props`, so a stale pin added here fails the suite it is meant to support.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj
```

Expected: FAIL to compile — no `NuGetBuildLevelUpdatesRule` constructor takes `(NuGetVersionCache, NuGetFloorCatalog, TimeProvider)`.

- [ ] **Step 3: Write the implementation**

Replace the body of `NuGetPackageUpdateRuleBase.cs` with:

```csharp
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using NuGet.Versioning;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Base class for rules that enforce NuGet package freshness by semantic update level.
/// </summary>
/// <remarks>
/// <para>
/// Asks two questions. Are you behind the estate — a version some repository of ours already runs?
/// That fails immediately, because it is a fact about us and somebody has already proven it works.
/// Are you behind nuget.org? That fails only after a grace period measured from the release's own
/// publication date, so drift still has consequences without handing the verdict to whoever
/// published this morning.
/// </para>
/// <para>
/// Neither question touches the network. Resolving "latest" live made an assessment depend on what
/// strangers published that day, and turned repositories red without a line of code changing.
/// </para>
/// </remarks>
public abstract class NuGetPackageUpdateRuleBase : RuleBase
{
	private readonly NuGetVersionCache _cache;
	private readonly NuGetFloorCatalog _floors;
	private readonly TimeProvider _timeProvider;

	/// <summary>
	/// Initializes a new instance using the shared stores. This is the constructor
	/// <see cref="RuleRegistry"/> uses, via <c>Activator.CreateInstance</c>.
	/// </summary>
	protected NuGetPackageUpdateRuleBase()
		: this(NuGetVersionCache.Default, NuGetFloorCatalog.Default, TimeProvider.System)
	{
	}

	/// <summary>
	/// Initializes a new instance with explicit stores and clock, for tests.
	/// </summary>
	/// <param name="cache">The committed upstream snapshot.</param>
	/// <param name="floors">The estate-learned floors.</param>
	/// <param name="timeProvider">The clock the grace period is measured against.</param>
	protected NuGetPackageUpdateRuleBase(
		NuGetVersionCache cache,
		NuGetFloorCatalog floors,
		TimeProvider timeProvider)
	{
		_cache = cache;
		_floors = floors;
		_timeProvider = timeProvider;
	}

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.NuGetHygiene;

	/// <summary>
	/// Gets the update level this rule enforces.
	/// </summary>
	protected abstract PackageUpdateLevel TargetUpdateLevel { get; }

	/// <summary>
	/// Gets the user-facing label for the update level.
	/// </summary>
	protected abstract string UpdateLevelDisplayName { get; }

	/// <summary>
	/// Gets how long a published release may go un-adopted before it becomes a failure.
	/// </summary>
	protected abstract int GraceDays { get; }

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var packageReferences = PackageReferenceScanner.Scan(context);
		if (packageReferences.Count == 0)
		{
			return Task.FromResult(Pass("No explicit NuGet package versions were found to evaluate."));
		}

		var behindEstate = new List<string>();
		var behindUpstream = new List<string>();
		var pending = new List<string>();
		var now = _timeProvider.GetUtcNow();

		foreach (var reference in packageReferences)
		{
			cancellationToken.ThrowIfCancellationRequested();

			// Raises the floor for subsequent runs only; this run's floor was frozen at load.
			_floors.Observe(reference.PackageId, reference.CurrentVersion, context.FullName);

			if (!NuGetVersion.TryParse(reference.CurrentVersion, out var current))
			{
				continue;
			}

			// Consistency: behind a version the estate already runs.
			var floor = _floors.GetFloor(reference.PackageId);
			if (floor is not null
				&& NuGetVersion.TryParse(floor, out var floorVersion)
				&& NuGetVersionChecker.ClassifyUpdateLevel(current, floorVersion) == TargetUpdateLevel)
			{
				behindEstate.Add($"{reference.PackageId} {current.ToNormalizedString()} → {floor} ({reference.FilePath})");
				continue;
			}

			// Freshness: behind nuget.org for longer than this level's grace.
			if (!_cache.TryGet(reference.PackageId, out var snapshot)
				|| !NuGetVersion.TryParse(snapshot.LatestVersion, out var latest)
				|| NuGetVersionChecker.ClassifyUpdateLevel(current, latest) != TargetUpdateLevel)
			{
				continue;
			}

			var age = now - snapshot.Published;
			var entry = $"{reference.PackageId} {current.ToNormalizedString()} → {snapshot.LatestVersion} ({reference.FilePath})";

			if (age.TotalDays > GraceDays)
			{
				behindUpstream.Add($"{entry}, published {age.Days} days ago");
			}
			else
			{
				pending.Add(entry);
			}
		}

		if (behindEstate.Count == 0 && behindUpstream.Count == 0)
		{
			// Drift inside the grace period is always reported, so it is visible before it is a failure.
			return Task.FromResult(pending.Count == 0
				? Pass($"No {UpdateLevelDisplayName} NuGet package updates are overdue.")
				: Pass($"No {UpdateLevelDisplayName} NuGet package updates are overdue. Available within the {GraceDays}-day grace period: {string.Join("; ", pending)}"));
		}

		var messages = new List<string>();
		if (behindEstate.Count > 0)
		{
			messages.Add($"behind the estate: {string.Join("; ", behindEstate)}");
		}

		if (behindUpstream.Count > 0)
		{
			messages.Add($"overdue against nuget.org: {string.Join("; ", behindUpstream)}");
		}

		return Task.FromResult(Fail(
			$"The following NuGet packages have {UpdateLevelDisplayName} updates outstanding — {string.Join(", ", messages)}",
			new RuleAdvisory
			{
				Summary = $"Update the listed packages to at least the version the estate already uses, and adopt {UpdateLevelDisplayName} releases within {GraceDays} days.",
				Detail = $"A package below the estate floor is behind a version another repository of ours already runs. A package past its {GraceDays}-day grace period has been behind a published release for too long. Update the listed versions in `Directory.Packages.props` or the affected project files.",
				Data = new()
				{
					["remediation_type"] = "update_package_versions",
					["behind_estate"] = behindEstate.ToArray(),
					["behind_upstream"] = behindUpstream.ToArray(),
					["grace_days"] = GraceDays
				}
			}));
	}
}
```

Then add the grace to each rule. In `NuGetBuildLevelUpdatesRule.cs`, replace the two constructors with one forwarding constructor and add the override:

```csharp
	/// <summary>
	/// Initializes a new instance of the <see cref="NuGetBuildLevelUpdatesRule"/> class.
	/// </summary>
	public NuGetBuildLevelUpdatesRule()
	{
	}

	/// <summary>
	/// Initializes a new instance with explicit stores and clock, for tests.
	/// </summary>
	/// <param name="cache">The committed upstream snapshot.</param>
	/// <param name="floors">The estate-learned floors.</param>
	/// <param name="timeProvider">The clock the grace period is measured against.</param>
	public NuGetBuildLevelUpdatesRule(
		NuGetVersionCache cache,
		NuGetFloorCatalog floors,
		TimeProvider timeProvider)
		: base(cache, floors, timeProvider)
	{
	}

	/// <inheritdoc />
	protected override int GraceDays => 30;
```

Repeat for `NuGetMinorLevelUpdatesRule` (`GraceDays => 90`) and `NuGetMajorLevelUpdatesRule` (`GraceDays => 365`), each with its own class name in the constructors.

Delete the now-unused `versionStatusResolver` constructors from all three rules and from the base.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*NuGetPackageUpdateGateTests"
```

Expected: `total: 6, failed: 0`.

`RuleEvaluationTests.cs:1350` and `:1370` construct these rules with the old resolver delegate and will no longer compile. Rewrite those two tests to use the new constructor with a `NuGetVersionCache` built by `CacheWith`-style setup, keeping their original intent.

- [ ] **Step 5: Run the full suite**

```bash
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe
```

Expected: no failures except possibly `SelfAssessmentTests` / `GitHubIntegrationTests`, which Task 7 addresses.

- [ ] **Step 6: Commit**

```bash
git add PanoramicData.NugetManagement/Rules/NuGetHygiene/ PanoramicData.NugetManagement.Test/NuGetPackageUpdateGateTests.cs PanoramicData.NugetManagement.Test/RuleEvaluationTests.cs Directory.Packages.props PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj
git commit -m "Gate package freshness on the estate and a grace period, not on the network

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: Rate-limited refresher

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Services/NuGetVersionRefresher.cs`
- Modify: `PanoramicData.NugetManagement.Web/Program.cs:48`
- Test: `PanoramicData.NugetManagement.Test/NuGetVersionRefresherTests.cs`

**Interfaces:**
- Consumes: `NuGetVersionCache.Update/Persist` (Task 2), `NuGetVersionChecker.GetLatestStableWithPublishedAsync` (Task 4).
- Produces: `NuGetVersionRefresher(NuGetVersionCache cache, Func<string, CancellationToken, Task<(string, DateTimeOffset)?>> lookup, TimeProvider timeProvider, ILogger<NuGetVersionRefresher> logger)`, `Task<int> RefreshAsync(IEnumerable<string> packageIds, CancellationToken)` returning the number of changed packages.

- [ ] **Step 1: Write the failing test**

Create `PanoramicData.NugetManagement.Test/NuGetVersionRefresherTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests the only component permitted to contact nuget.org for dependency versions. No test here
/// touches the network: the lookup is a delegate.
/// </summary>
public class NuGetVersionRefresherTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly DateTimeOffset _published = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task ShouldRecordEveryPackageItLooksUp()
	{
		var cache = new NuGetVersionCache(null);
		var refresher = Refresher(cache, (id, _) => Task.FromResult<(string, DateTimeOffset)?>(("3.0.43", _published)));

		await refresher.RefreshAsync(["Codacy.Api", "Octokit"], TestContext.Current.CancellationToken);

		cache.TryGet("Codacy.Api", out _).Should().BeTrue();
		cache.TryGet("Octokit", out _).Should().BeTrue();
	}

	[Fact]
	public async Task ShouldReportHowManyPackagesActuallyChanged()
	{
		var cache = new NuGetVersionCache(null);
		var refresher = Refresher(cache, (id, _) => Task.FromResult<(string, DateTimeOffset)?>(("3.0.43", _published)));

		await refresher.RefreshAsync(["Codacy.Api"], TestContext.Current.CancellationToken);

		var secondSweep = await refresher.RefreshAsync(["Codacy.Api"], TestContext.Current.CancellationToken);

		secondSweep.Should().Be(0, "an unchanged sweep must not dirty the committed file");
	}

	[Fact]
	public async Task ShouldQueryEachPackageOnlyOnce()
	{
		var calls = new List<string>();
		var cache = new NuGetVersionCache(null);
		var refresher = Refresher(cache, (id, _) =>
		{
			lock (calls)
			{
				calls.Add(id);
			}

			return Task.FromResult<(string, DateTimeOffset)?>(("3.0.43", _published));
		});

		await refresher.RefreshAsync(["Codacy.Api", "codacy.api", "Codacy.Api"], TestContext.Current.CancellationToken);

		calls.Should().ContainSingle("duplicate ids across repositories are the same question");
	}

	[Fact]
	public async Task APackageItCannotReadShouldLeaveTheCacheAsItWas()
	{
		var cache = new NuGetVersionCache(null);
		cache.Update("Codacy.Api", "3.0.42", _published, _published);

		var refresher = Refresher(cache, (_, _) => Task.FromResult<(string, DateTimeOffset)?>(null));
		await refresher.RefreshAsync(["Codacy.Api"], TestContext.Current.CancellationToken);

		cache.TryGet("Codacy.Api", out var snapshot);
		snapshot.LatestVersion.Should().Be("3.0.42", "a version known a minute ago beats no version at all");
	}

	[Fact]
	public async Task AFailingLookupShouldNotAbandonTheRestOfTheSweep()
	{
		var cache = new NuGetVersionCache(null);
		var refresher = Refresher(cache, (id, _) => id == "Codacy.Api"
			? throw new HttpRequestException("nuget.org is down")
			: Task.FromResult<(string, DateTimeOffset)?>(("1.0.0", _published)));

		await refresher.RefreshAsync(["Codacy.Api", "Octokit"], TestContext.Current.CancellationToken);

		cache.TryGet("Octokit", out _).Should().BeTrue();
	}

	private static NuGetVersionRefresher Refresher(
		NuGetVersionCache cache,
		Func<string, CancellationToken, Task<(string, DateTimeOffset)?>> lookup)
		=> new(
			cache,
			lookup,
			new FakeTimeProvider(_published),
			NullLogger<NuGetVersionRefresher>.Instance);
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj
```

Expected: FAIL to compile — `The type or namespace name 'NuGetVersionRefresher' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `PanoramicData.NugetManagement.Web/Services/NuGetVersionRefresher.cs`:

```csharp
using Microsoft.Extensions.Logging;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Refreshes the committed record of what nuget.org has published.
/// </summary>
/// <remarks>
/// The only component that contacts nuget.org for dependency versions. The rules read the cache and
/// never make a request, which is what makes an assessment reproducible and offline-tolerant, and
/// what removed one round trip per package reference per rule from every run.
/// </remarks>
public sealed class NuGetVersionRefresher
{
	/// <summary>The most requests in flight at once. nuget.org is a shared service, not ours.</summary>
	private const int _maximumConcurrency = 4;

	private readonly NuGetVersionCache _cache;
	private readonly Func<string, CancellationToken, Task<(string Version, DateTimeOffset Published)?>> _lookup;
	private readonly TimeProvider _timeProvider;
	private readonly ILogger<NuGetVersionRefresher> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="NuGetVersionRefresher"/> class.
	/// </summary>
	/// <param name="cache">The cache to fill.</param>
	/// <param name="lookup">Reads a package's latest stable version and publication date.</param>
	/// <param name="timeProvider">The clock used to stamp genuine changes.</param>
	/// <param name="logger">The logger.</param>
	public NuGetVersionRefresher(
		NuGetVersionCache cache,
		Func<string, CancellationToken, Task<(string Version, DateTimeOffset Published)?>> lookup,
		TimeProvider timeProvider,
		ILogger<NuGetVersionRefresher> logger)
	{
		_cache = cache;
		_lookup = lookup;
		_timeProvider = timeProvider;
		_logger = logger;
	}

	/// <summary>
	/// Refreshes every distinct package id given, and persists only if something changed.
	/// </summary>
	/// <param name="packageIds">The package ids to refresh; duplicates are asked once.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>How many packages had a different version from the one already held.</returns>
	public async Task<int> RefreshAsync(IEnumerable<string> packageIds, CancellationToken cancellationToken)
	{
		var distinct = packageIds
			.Where(id => !string.IsNullOrWhiteSpace(id))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		var now = _timeProvider.GetUtcNow();
		var changed = 0;

		using var limiter = new SemaphoreSlim(_maximumConcurrency);

		var sweeps = distinct.Select(async packageId =>
		{
			await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				var latest = await _lookup(packageId, cancellationToken).ConfigureAwait(false);
				if (latest is null)
				{
					// A version known a minute ago beats no version at all: blanking it would turn a
					// transient nuget.org failure into "this package has never been published".
					return;
				}

				if (_cache.Update(packageId, latest.Value.Version, latest.Value.Published, now))
				{
					Interlocked.Increment(ref changed);
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// One unreadable package must not abandon the sweep: the rest of the estate still
				// deserves an up-to-date answer.
				_logger.LogWarning(ex, "Could not refresh {PackageId}", packageId);
			}
			finally
			{
				limiter.Release();
			}
		});

		await Task.WhenAll(sweeps).ConfigureAwait(false);

		if (changed > 0)
		{
			_cache.Persist();
			_logger.LogInformation("Refreshed {Changed} of {Total} NuGet package versions.", changed, distinct.Count);
		}

		return changed;
	}
}
```

Register it in `Program.cs` beside the other singletons (after line 48):

```csharp
builder.Services.AddSingleton(_ => NuGetVersionCache.Default);
builder.Services.AddSingleton<NuGetVersionRefresher>(sp =>
{
	var checker = new NuGetVersionChecker(sp.GetRequiredService<ILogger<NuGetVersionChecker>>());
	return new NuGetVersionRefresher(
		NuGetVersionCache.Default,
		checker.GetLatestStableWithPublishedAsync,
		TimeProvider.System,
		sp.GetRequiredService<ILogger<NuGetVersionRefresher>>());
});
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*NuGetVersionRefresherTests"
```

Expected: `total: 5, failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Services/NuGetVersionRefresher.cs PanoramicData.NugetManagement.Web/Program.cs PanoramicData.NugetManagement.Test/NuGetVersionRefresherTests.cs
git commit -m "Give nuget.org a single, rate-limited caller

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: Stop the self-assessment asserting the calendar

**Files:**
- Modify: `PanoramicData.NugetManagement.Test/SelfAssessmentTests.cs`
- Modify: `PanoramicData.NugetManagement.Test/GitHubIntegrationTests.cs`

**Interfaces:**
- Consumes: the rule ids `PKG-05`, `PKG-06`, `PKG-07`.
- Produces: nothing.

- [ ] **Step 1: Write the change**

In both tests, exclude the three grace-dependent rules from the "everything passes" assertion and report them instead. Add near the assertion in each file:

```csharp
	/// <summary>
	/// The rules whose verdict depends on how long ago somebody else published, rather than on
	/// anything in this repository.
	/// </summary>
	/// <remarks>
	/// A grace period is a clock: a release nobody adopts will eventually breach it and turn this
	/// suite red with no code change. That is the rule working as intended, but "all rules pass"
	/// would then be an assertion about the calendar. Their results are printed so drift is still
	/// visible here.
	/// </remarks>
	private static readonly string[] _graceDependentRuleIds = ["PKG-05", "PKG-06", "PKG-07"];
```

and filter the failures before asserting:

```csharp
		var failures = assessment.RuleResults
			.Where(r => !r.Passed)
			.ToList();

		foreach (var graced in failures.Where(r => _graceDependentRuleIds.Contains(r.RuleId)))
		{
			Output.WriteLine($"[grace] {graced.RuleId}: {graced.Message}");
		}

		failures
			.Where(r => !_graceDependentRuleIds.Contains(r.RuleId))
			.Should().BeEmpty();
```

Adjust the surrounding variable names to match each file — read the existing assertion first and keep its shape.

- [ ] **Step 2: Run both test classes**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*SelfAssessmentTests"
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*GitHubIntegrationTests"
```

Expected: both pass, with any `[grace]` lines printed rather than failing.

- [ ] **Step 3: Commit**

```bash
git add PanoramicData.NugetManagement.Test/SelfAssessmentTests.cs PanoramicData.NugetManagement.Test/GitHubIntegrationTests.cs
git commit -m "Stop the self-assessment asserting what strangers published

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: Seed and commit the two stores

**Files:**
- Create: `nuget-versions.json`, `nuget-floors.json` (repository root)
- Modify: `.gitignore` if either name is currently ignored (check first)

**Interfaces:**
- Consumes: everything above.
- Produces: the committed seed data.

- [ ] **Step 1: Confirm neither file is ignored**

```bash
git check-ignore -v nuget-versions.json nuget-floors.json || echo "neither is ignored"
```

Expected: `neither is ignored`. If either is listed, remove the pattern — these files must be committed or the whole design does not work.

- [ ] **Step 2: Seed the floors by running one assessment**

Run the app or the self-assessment test, which calls `Observe` for every package reference:

```bash
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*SelfAssessmentTests"
cat nuget-floors.json
```

Expected: a JSON object mapping package ids to versions.

- [ ] **Step 3: Seed the cache with one refresher sweep**

Start the web app, let one sweep complete, then stop it and inspect:

```bash
cat nuget-versions.json
```

Expected: an entry per package with `latestVersion`, `published` and `refreshedAtUtc`.

- [ ] **Step 4: Run the full suite against the seeded data**

```bash
./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe
```

Expected: no failures.

- [ ] **Step 5: Commit**

```bash
git add nuget-versions.json nuget-floors.json
git commit -m "Seed the version cache and the estate floor

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage**

| Spec section | Task |
|---|---|
| `NuGetVersionCache`, committed, frozen, `TryGet` | 1 |
| Persist only on version change | 2 |
| `NuGetFloorCatalog`, frozen baseline, `Observe`, `RecentBumps` | 3 |
| Published date from `IPackageSearchMetadata.Published` | 4 |
| Rule decision order: observe → floor → grace | 5 |
| Grace table 30/90/365 | 5 |
| Rate-limited refresher, background and one-shot | 6 |
| Error handling: miss, corrupt, stale, refresher failure, persist failure | 1, 3, 6 |
| Self-assessment stops asserting grace-dependent rules | 7 |
| Rollout: seed then commit | 8 |
| `PKG-11`/`PKG-12` untouched | out of scope, no task |

**Known gap:** the spec says the refresher is available as a one-shot command; Task 6 builds the callable `RefreshAsync` and registers the service, but does not add a CLI entry point. Add one when a host for it exists — the method is the reusable part, and no current caller needs a command line.
