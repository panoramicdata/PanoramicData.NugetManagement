# Dependabot Pull Request Adoption Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Fix act on the Dependabot pull requests it currently ignores — by reading grouped pull requests from their bodies, and by adopting a valid bump once the pull request is old enough that no grace period is still protecting anything.

**Architecture:** Triage stays pure and gains a clock: it folds a multi-bump proposal into one verdict, and for an adoptable pull request it computes an *adoption plan* — the exact advisory payloads a failing rule would have produced. The web side applies that plan through the two writers that already exist (`update_package_versions`, `replace_regex_in_files`) by synthesizing a failing `RuleResult`, then closes the pull request only if the write actually applied something.

**Tech Stack:** .NET 10, C# 13, xunit v3 with Microsoft.Testing.Platform, AwesomeAssertions (`.Should()`), `Microsoft.Extensions.TimeProvider.Testing` (`FakeTimeProvider`), Octokit, `gh` CLI for fixture capture.

**Spec:** `docs/superpowers/specs/2026-09-03-dependabot-adoption-design.md`

## Global Constraints

- **Never run `dotnet test`.** It reports `Zero tests ran` and exits 5 in this repository even when the suite is healthy. Run the test binary directly: `./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe`, filtering with `--filter-class "*ClassName"` or `--filter-method "*MethodName*"`. The bare `-class` / `-method` flags print help and run nothing.
- **If the web app is running it holds the build lock.** Build with `-p:OutDir` into an **in-repo** directory — `PanoramicData.NugetManagement.Test/bin/Verify/net10.0/` — and run the exe from there. An out-of-tree output directory (the scratchpad included) produces ~18 spurious "Could not find the repository root" failures, because several suites walk up from `AppContext.BaseDirectory`.
- **One pre-existing failure is expected and is not a regression:** `DetachedProcessLaunchTests.Start_FromInsideAJobObject_LaunchesTheChildOutsideIt` reports `FAIL_SKIP` when run from a tool call. A full run with exactly this one failure is a green run.
- **`Output.WriteLine` is only shown for tests that FAIL.** To read a passing probe's output, end it with `Assert.Fail(report)`.
- **`DependabotVerdict.Adoptable` MUST be appended as the last enum member.** `RepositoryIssue.TriageVerdict` is persisted to the row cache as a JSON *number*; inserting a member mid-enum silently rewrites the meaning of every cached verdict.
- **Adoption age threshold:** `TimeSpan.FromDays(60)`, as a constant with a test-only constructor override. Do **not** add a `RuntimeSettings` property — a new one must also be added to the hand-written `SaveToDisk` snapshot or every save erases it.
- **Close marker for adoption:** `<!-- nugetmgmt:closed:adopted -->`, distinct from the existing `DependabotTriageRunner.ClosedMarker` (`<!-- nugetmgmt:closed:already-satisfied -->`).
- **Other Claude sessions commit to `main` in this repository** and the dashboard's own Commit & Push can claim your uncommitted files. Work in a git worktree, and stage explicit pathspecs — never `git add -A`.
- Tab indentation, file-scoped namespaces, XML doc comments on every public member (the projects have `GenerateDocumentationFile` on and warnings as errors). Match the surrounding prose style in doc comments: say *why*, not just *what*.

---

## File Structure

**Core (`PanoramicData.NugetManagement`) — pure, no I/O:**

| File | Responsibility | Change |
|---|---|---|
| `Models/RepositoryIssue.cs` | One open issue/PR | Add `Body` (`[JsonIgnore]`) |
| `Models/DependabotProposal.cs` | What a PR proposes | Reshape to `Number` + `Bumps` + `HtmlUrl`; add `DependabotBump` |
| `Models/DependabotVerdict.cs` | Triage conclusion | Append `Adoptable` |
| `Models/DependabotAdoptionPlan.cs` | The payloads adoption will write | **Create** |
| `Services/DependabotTitleParser.cs` | Read a PR's proposal | Rename to `DependabotProposalParser`; read body, fall back to title |
| `Services/DependabotTriageService.cs` | Reach a verdict per PR | Fold over bumps; add clock, age gate, `Adoptable`, plan |
| `Services/RepositoryIssueService.cs` | Build `RepositoryIssue` list | Map `Body` through |
| `Rules/CiWorkflow/ActionUsesPattern.cs` | How a `uses:` line is rewritten | **Create** (extracted from CI-12) |
| `Rules/CiWorkflow/CiActionVersionFloorRule.cs` | CI-12 | Call the extracted helper |

**Web (`PanoramicData.NugetManagement.Web`) — the side that writes:**

| File | Responsibility | Change |
|---|---|---|
| `Remediations/DependabotAdoptionRemediation.cs` | Apply an adoption plan via the existing writers | **Create** |
| `Services/IBumpAdopter.cs` | The port the runner calls to adopt | **Create** |
| `Services/DependabotTriageRunner.cs` | Carry out the verdicts | Per-bump sightings; adopt-then-close; `Adopted` count |
| `Services/WorkExecutors.cs` | The triage lane | Clone preconditions, pass the adopter, summary line |
| `Components/RepositoryIssuesView.razor` | Triage label in the tree | Add the `Adoptable` case |

**Tests (`PanoramicData.NugetManagement.Test`):**

| File | Change |
|---|---|
| `Fixtures/DependabotBodies/*.md` | **Create** — the five real PR bodies |
| `DependabotTitleParserTests.cs` | Rename to `DependabotProposalParserTests.cs`, extend |
| `DependabotTriageServiceTests.cs` | Folding, age gate, plan payloads |
| `DependabotTriageRunnerTests.cs` | Adopt-then-close, and the withheld close |
| `DependabotAdoptionRemediationTests.cs` | **Create** — real file writes in a temp tree |
| `PanoramicData.NugetManagement.Test.csproj` | Copy the fixtures to the output directory |

Adoption's judgement lives in core (pure, fully unit-testable) and its *writing* lives in web. That is the existing rules-in-core / remediations-in-web seam, and it is what keeps `DependabotTriageRunner` free of file I/O — the property its own documentation claims.

---

### Task 1: Capture the five real pull request bodies

Everything downstream is built on Dependabot's body format. The spec's assumed wording is an expectation, not a fact, and a parser built on a guessed format fails silent on exactly the pull requests it was written for. So the real bodies come first and become the fixtures.

**Files:**
- Create: `PanoramicData.NugetManagement.Test/Fixtures/DependabotBodies/pr-6.md`
- Create: `PanoramicData.NugetManagement.Test/Fixtures/DependabotBodies/pr-26.md`
- Create: `PanoramicData.NugetManagement.Test/Fixtures/DependabotBodies/pr-28.md`
- Create: `PanoramicData.NugetManagement.Test/Fixtures/DependabotBodies/pr-30.md`
- Create: `PanoramicData.NugetManagement.Test/Fixtures/DependabotBodies/pr-33.md`
- Create: `PanoramicData.NugetManagement.Test/DependabotFixtures.cs`
- Modify: `PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj`

**Interfaces:**
- Consumes: nothing.
- Produces: `DependabotFixtures.Body(int pullRequestNumber)` returning `string`; `DependabotFixtures.Numbers` returning `int[]`.

- [ ] **Step 1: Fetch the five bodies**

```bash
mkdir -p PanoramicData.NugetManagement.Test/Fixtures/DependabotBodies
for n in 6 26 28 30 33; do
  gh api "repos/panoramicdata/Highlight.Api/pulls/$n" --jq .body \
    > "PanoramicData.NugetManagement.Test/Fixtures/DependabotBodies/pr-$n.md"
done
wc -l PanoramicData.NugetManagement.Test/Fixtures/DependabotBodies/*.md
```

If `gh` is not authenticated, run `gh auth status` and stop with that message rather than inventing fixture content. **Fabricated fixtures would defeat the entire point of this task.**

- [ ] **Step 2: Read what Dependabot actually wrote**

```bash
grep -nE '^(Updates|Bumps)' PanoramicData.NugetManagement.Test/Fixtures/DependabotBodies/*.md
```

Record the answers to these three questions in the commit message, because Tasks 3 and 6 depend on them:

1. Is the per-dependency line `Updates \`Name\` from X to Y`, or something else?
2. Does the single-dependency body (`pr-6.md`) use `Bumps [Name](url) from X to Y.` with a trailing full stop?
3. Does any line carry an ` in /dir` suffix, and does any name a GitHub Action (contains `/`)?

**If the format differs from the spec's assumption, the regexes in Task 3 change to match what is actually there.** The fixtures are the authority, not the spec.

- [ ] **Step 3: Make the fixtures reachable from a test**

Add to `PanoramicData.NugetManagement.Test.csproj`, in the existing `ItemGroup` that holds the `xunit.runner.json` entry:

```xml
		<None Include="Fixtures\**\*">
			<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
		</None>
```

Create `PanoramicData.NugetManagement.Test/DependabotFixtures.cs`:

```csharp
namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// The real bodies of the Dependabot pull requests standing open against
/// <c>panoramicdata/Highlight.Api</c>, captured verbatim.
/// </summary>
/// <remarks>
/// Committed rather than fetched at test time: the parser has to keep working against what Dependabot
/// actually writes, and a test that reaches GitHub for it would pass or fail on network weather and
/// would silently start testing a different format the day those pull requests are closed.
/// </remarks>
internal static class DependabotFixtures
{
	/// <summary>The pull request numbers a body was captured for.</summary>
	public static readonly int[] Numbers = [6, 26, 28, 30, 33];

	/// <summary>
	/// The captured body of one pull request.
	/// </summary>
	/// <param name="pullRequestNumber">The pull request number, one of <see cref="Numbers"/>.</param>
	public static string Body(int pullRequestNumber)
	{
		var path = Path.Combine(
			AppContext.BaseDirectory,
			"Fixtures",
			"DependabotBodies",
			$"pr-{pullRequestNumber}.md");

		return File.Exists(path)
			? File.ReadAllText(path)
			: throw new FileNotFoundException(
				$"The captured body for pull request #{pullRequestNumber} is missing. It should have "
					+ "been committed with the tests; see docs/superpowers/plans/"
					+ "2026-09-03-dependabot-adoption.md Task 1.",
				path);
	}
}
```

- [ ] **Step 4: Write the failing test**

Create `PanoramicData.NugetManagement.Test/DependabotFixturesTests.cs`:

```csharp
namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="DependabotFixtures"/>: that the captured bodies reached the output directory,
/// so a parser test failing later is a parser problem rather than a missing file.
/// </summary>
public class DependabotFixturesTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void Body_ForEveryCapturedPullRequest_IsNotEmpty()
	{
		foreach (var number in DependabotFixtures.Numbers)
		{
			DependabotFixtures
				.Body(number)
				.Should().NotBeNullOrWhiteSpace($"pull request #{number}'s body was captured in Task 1");
		}
	}

	[Fact]
	public void Body_ForEveryCapturedPullRequest_NamesAtLeastOneDependencyMove()
		=> DependabotFixtures.Numbers
			.Should().AllSatisfy(number => DependabotFixtures
				.Body(number)
				.Should().MatchRegex(
					"(?i)(updates|bumps)",
					"a Dependabot body states what it moves, and the parser reads those lines"));
}
```

- [ ] **Step 5: Build and run it**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj \
  -p:OutDir="$(pwd)/PanoramicData.NugetManagement.Test/bin/Verify/net10.0/"
./PanoramicData.NugetManagement.Test/bin/Verify/net10.0/PanoramicData.NugetManagement.Test.exe \
  --filter-class "*DependabotFixturesTests"
```

Expected: PASS, 2 tests. A `FileNotFoundException` means Step 3's `None Include` did not take effect — check the glob and rebuild.

- [ ] **Step 6: Commit**

```bash
git add PanoramicData.NugetManagement.Test/Fixtures \
        PanoramicData.NugetManagement.Test/DependabotFixtures.cs \
        PanoramicData.NugetManagement.Test/DependabotFixturesTests.cs \
        PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj
git commit -m "test: capture the five real Dependabot pull request bodies as fixtures

<record here the answers to Step 2's three questions>"
```

---

### Task 2: Carry the pull request body through to triage

The body is already fetched — `OctokitGitHubIssueApi` puts `issue.Body` on every `GitHubOpenItem` for the gap-issue marker check — but `RepositoryIssueService` drops it when it builds `RepositoryIssue`. Triage sees only titles.

**Files:**
- Modify: `PanoramicData.NugetManagement/Models/RepositoryIssue.cs`
- Modify: `PanoramicData.NugetManagement/Services/RepositoryIssueService.cs:123-131`
- Test: `PanoramicData.NugetManagement.Test/RepositoryIssueBodyTests.cs` (create)

**Interfaces:**
- Consumes: `GitHubOpenItem.Body` (`string?`), already present.
- Produces: `RepositoryIssue.Body` (`string?`, `[JsonIgnore]`, `init`).

- [ ] **Step 1: Write the failing test**

Create `PanoramicData.NugetManagement.Test/RepositoryIssueBodyTests.cs`:

```csharp
using System.Text.Json;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="RepositoryIssue.Body"/>: triage needs it within the pass that fetched it, and
/// the row cache must not carry it.
/// </summary>
public class RepositoryIssueBodyTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RepositoryIssue Issue(string? body) => new()
	{
		Number = 30,
		Title = "Bump Microsoft.Extensions.DependencyInjection and 2 others",
		IsPullRequest = true,
		HtmlUrl = "https://github.com/panoramicdata/Highlight.Api/pull/30",
		AuthorLogin = "dependabot[bot]",
		CreatedAtUtc = new DateTimeOffset(2026, 6, 13, 0, 0, 0, TimeSpan.Zero),
		Body = body
	};

	[Fact]
	public void Body_WhenSet_IsReadable()
		=> Issue("Updates `Something` from 1.0.0 to 2.0.0")
			.Body.Should().Be("Updates `Something` from 1.0.0 to 2.0.0");

	[Fact]
	public void Body_IsNotPersisted()
		=> JsonSerializer
			.Serialize(Issue("a body long enough to notice in a cache file"))
			.Should().NotContain(
				"long enough to notice",
				"Dependabot bodies carry whole changelogs, and the row cache holds every open item of "
					+ "every repository — persisting them would inflate it to store text only ever read "
					+ "during the pass that fetched it");
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj \
  -p:OutDir="$(pwd)/PanoramicData.NugetManagement.Test/bin/Verify/net10.0/"
```

Expected: FAIL to compile — `'RepositoryIssue' does not contain a definition for 'Body'`.

- [ ] **Step 3: Add the property**

In `PanoramicData.NugetManagement/Models/RepositoryIssue.cs`, directly after the `Title` property:

```csharp
	/// <summary>
	/// The item's body as GitHub reported it, or null when the item did not come from a fetch that
	/// read one.
	/// </summary>
	/// <remarks>
	/// Not persisted. Dependabot writes whole changelogs into a pull request body, and the row cache
	/// holds every open item of every repository — carrying them would inflate the cache by orders of
	/// magnitude to store text that is only ever read during the pass that fetched it.
	/// <para>
	/// The consequence is that a restored row has titles but no bodies, so a grouped pull request
	/// cannot be judged from cache alone: it parses to nothing and is left strictly alone, which is
	/// the safe direction and the behaviour that existed before bodies were read at all.
	/// </para>
	/// </remarks>
	[JsonIgnore]
	public string? Body { get; init; }
```

- [ ] **Step 4: Map it through**

In `PanoramicData.NugetManagement/Services/RepositoryIssueService.cs`, in the object initializer at line 123, add after `CreatedAtUtc`:

```csharp
			Body = item.Body,
```

- [ ] **Step 5: Run the tests**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj \
  -p:OutDir="$(pwd)/PanoramicData.NugetManagement.Test/bin/Verify/net10.0/"
./PanoramicData.NugetManagement.Test/bin/Verify/net10.0/PanoramicData.NugetManagement.Test.exe \
  --filter-class "*RepositoryIssueBodyTests"
./PanoramicData.NugetManagement.Test/bin/Verify/net10.0/PanoramicData.NugetManagement.Test.exe \
  --filter-class "*RepositoryIssueSeverityTests"
```

Expected: PASS for both. The second confirms nothing that reads `RepositoryIssue` regressed.

- [ ] **Step 6: Commit**

```bash
git add PanoramicData.NugetManagement/Models/RepositoryIssue.cs \
        PanoramicData.NugetManagement/Services/RepositoryIssueService.cs \
        PanoramicData.NugetManagement.Test/RepositoryIssueBodyTests.cs
git commit -m "feat: carry a pull request body through to triage, without caching it"
```

---

### Task 3: Read grouped pull requests from the body

`DependabotProposal` becomes a pull request plus a list of bumps, and the parser reads the body — falling back to the title so a restored row with no body still parses a single-dependency pull request exactly as it does today.

**Files:**
- Modify: `PanoramicData.NugetManagement/Models/DependabotProposal.cs`
- Rename: `PanoramicData.NugetManagement/Services/DependabotTitleParser.cs` → `DependabotProposalParser.cs`
- Rename: `PanoramicData.NugetManagement.Test/DependabotTitleParserTests.cs` → `DependabotProposalParserTests.cs`

**Interfaces:**
- Consumes: `RepositoryIssue.Body` (Task 2), `DependabotFixtures.Body(int)` (Task 1).
- Produces:
  - `record DependabotBump(DependencyRef Dependency, string FromVersion, string ToVersion, string? Directory)`
  - `record DependabotProposal(int Number, IReadOnlyList<DependabotBump> Bumps, string HtmlUrl)`
  - `DependabotProposalParser.Parse(RepositoryIssue) → DependabotProposal?`
  - `DependabotProposalParser.DependabotLogin` (unchanged constant, `"dependabot[bot]"`)

- [ ] **Step 1: Reshape the model**

Replace the body of `PanoramicData.NugetManagement/Models/DependabotProposal.cs`:

```csharp
namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// One dependency a Dependabot pull request would move.
/// </summary>
/// <param name="Dependency">The dependency.</param>
/// <param name="FromVersion">The version Dependabot believes is declared.</param>
/// <param name="ToVersion">The version it would move to.</param>
/// <param name="Directory">The sub-directory it applies to, or null when none was named.</param>
public sealed record DependabotBump(
	DependencyRef Dependency,
	string FromVersion,
	string ToVersion,
	string? Directory);

/// <summary>
/// What one Dependabot pull request proposes.
/// </summary>
/// <param name="Number">The pull request number.</param>
/// <param name="Bumps">
/// Every dependency it would move, in the order the pull request lists them. Never empty: a pull
/// request proposing nothing readable parses to no proposal at all rather than an empty one, so that
/// "we read it and it said nothing" can never be mistaken for "it proposes nothing".
/// </param>
/// <param name="HtmlUrl">The pull request's web address.</param>
/// <remarks>
/// A list rather than a single dependency because Dependabot groups updates: a grouped pull request
/// moves several dependencies at once, and its title names neither all of them nor any version. A
/// single-dependency pull request is a proposal with one bump, so nothing downstream special-cases
/// either shape.
/// </remarks>
public sealed record DependabotProposal(
	int Number,
	IReadOnlyList<DependabotBump> Bumps,
	string HtmlUrl);
```

- [ ] **Step 2: Write the failing tests**

`git mv PanoramicData.NugetManagement.Test/DependabotTitleParserTests.cs PanoramicData.NugetManagement.Test/DependabotProposalParserTests.cs`, rename the class to `DependabotProposalParserTests`, add a `body` parameter to its `PullRequest` helper, and rewrite the existing assertions against `Bumps`. The helper becomes:

```csharp
	private static RepositoryIssue PullRequest(
		string title,
		string author = "dependabot[bot]",
		bool isPullRequest = true,
		string? body = null)
		=> new()
		{
			Number = 1,
			Title = title,
			IsPullRequest = isPullRequest,
			HtmlUrl = "https://github.com/panoramicdata/Athonet.Api/pull/1",
			AuthorLogin = author,
			CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
			Body = body
		};
```

An existing test converts like this — the single bump is now `Bumps[0]`:

```csharp
	[Fact]
	public void Parse_NuGetBumpInSubdirectory_ReadsPackageVersionsAndDirectory()
	{
		var proposal = DependabotProposalParser.Parse(
			PullRequest("Bump refit from 6.3.2 to 7.2.22 in /Athonet.Api"));

		proposal.Should().NotBeNull();
		proposal!.Bumps.Should().HaveCount(1);
		proposal.Bumps[0].Dependency.Should().Be(new DependencyRef(DependencyEcosystem.NuGet, "refit"));
		proposal.Bumps[0].FromVersion.Should().Be("6.3.2");
		proposal.Bumps[0].ToVersion.Should().Be("7.2.22");
		proposal.Bumps[0].Directory.Should().Be("/Athonet.Api");
	}
```

`Parse_GroupedBump_ReturnsNull` no longer describes the truth — a grouped pull request *with a body* now parses. Replace it with the pair below, keeping the null case for a grouped title with no body:

```csharp
	[Fact]
	public void Parse_GroupedTitleWithNoBody_ReturnsNull()
		=> DependabotProposalParser
			.Parse(PullRequest("Bump the nuget group with 3 updates"))
			.Should().BeNull(
				"a grouped title names no versions, and with no body there is nothing else to read — "
					+ "so it is left strictly alone, as it was before bodies were read at all");

	[Fact]
	public void Parse_RealGroupedBody_ReadsEveryDependency()
	{
		var proposal = DependabotProposalParser.Parse(PullRequest(
			"Bump Microsoft.Extensions.DependencyInjection and 2 others",
			body: DependabotFixtures.Body(30)));

		proposal.Should().NotBeNull();
		proposal!.Bumps.Should().HaveCount(
			3,
			"the title says 'and 2 others', so the body lists three dependencies");
		proposal.Bumps.Should().AllSatisfy(bump =>
		{
			bump.Dependency.Name.Should().NotBeNullOrWhiteSpace();
			bump.FromVersion.Should().NotBeNullOrWhiteSpace();
			bump.ToVersion.Should().NotBeNullOrWhiteSpace();
		});
	}

	[Fact]
	public void Parse_RealSingleDependencyBody_ReadsTheOneDependency()
	{
		var proposal = DependabotProposalParser.Parse(PullRequest(
			"Bump coverlet.collector from 8.0.1 to 10.0.0",
			body: DependabotFixtures.Body(6)));

		proposal.Should().NotBeNull();
		proposal!.Bumps.Should().HaveCount(1);
		proposal.Bumps[0].Dependency.Should().Be(
			new DependencyRef(DependencyEcosystem.NuGet, "coverlet.collector"));
		proposal.Bumps[0].FromVersion.Should().Be("8.0.1");
		proposal.Bumps[0].ToVersion.Should().Be("10.0.0");
	}

	[Fact]
	public void Parse_EveryCapturedBody_YieldsBumps()
		=> DependabotFixtures.Numbers
			.Should().AllSatisfy(number => DependabotProposalParser
				.Parse(PullRequest($"Bump something, pull request {number}", body: DependabotFixtures.Body(number)))
				.Should().NotBeNull($"pull request #{number}'s real body must be readable"));

	[Fact]
	public void Parse_BodyNamingAnAction_IsAGitHubAction()
	{
		var proposal = DependabotProposalParser.Parse(PullRequest(
			"Bump the actions group with 1 update",
			body: "Updates `actions/checkout` from 4 to 5"));

		proposal.Should().NotBeNull();
		proposal!.Bumps[0].Dependency.Should().Be(
			new DependencyRef(DependencyEcosystem.GitHubActions, "actions/checkout"),
			"a dependency name containing a slash is an action, not a NuGet package");
	}

	[Fact]
	public void Parse_BodyListingTheSameDependencyTwice_KeepsOneBump()
	{
		var proposal = DependabotProposalParser.Parse(PullRequest(
			"Bump the nuget group with 1 update",
			body: """
				Updates `Serilog` from 3.0.0 to 4.0.0
				Updates `Serilog` from 3.0.0 to 4.0.0
				"""));

		proposal.Should().NotBeNull();
		proposal!.Bumps.Should().HaveCount(
			1,
			"Dependabot repeats a dependency in the body when it appears in more than one manifest, "
				+ "and a duplicated bump would be written twice and counted twice");
	}

	[Fact]
	public void Parse_BodyWithNoRecognisableLine_ReturnsNull()
		=> DependabotProposalParser
			.Parse(PullRequest("Bump the nuget group with 3 updates", body: "Some prose and a table."))
			.Should().BeNull(
				"failing silent is the whole design: a body we cannot read must not become a proposal "
					+ "we act on");
```

- [ ] **Step 3: Run to verify they fail**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj \
  -p:OutDir="$(pwd)/PanoramicData.NugetManagement.Test/bin/Verify/net10.0/"
```

Expected: FAIL to compile — `DependabotProposalParser` does not exist, and `proposal.Dependency` no longer resolves anywhere it is still used.

- [ ] **Step 4: Write the parser**

`git mv PanoramicData.NugetManagement/Services/DependabotTitleParser.cs PanoramicData.NugetManagement/Services/DependabotProposalParser.cs`, then replace its contents:

```csharp
using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Reads what a Dependabot pull request proposes, from its body where there is one and its title
/// otherwise.
/// </summary>
/// <remarks>
/// The body is the authority because a grouped pull request's title is not readable: "Bump
/// Microsoft.Extensions.DependencyInjection and 2 others" names neither the other two dependencies
/// nor a single version, so no title pattern can recover them. Dependabot writes the full list into
/// the body, one line per dependency, and that is what this reads.
/// <para>
/// The title remains a fallback, for the case where a row was restored from the cache: bodies are not
/// persisted, and a single-dependency title is perfectly readable on its own. A grouped title with no
/// body yields nothing, which is exactly the behaviour that existed before bodies were read.
/// </para>
/// <para>
/// Still fails silent rather than open: anything unrecognised returns null and triage leaves that
/// pull request strictly alone. Guessing would mean closing pull requests nobody understood.
/// </para>
/// </remarks>
public static partial class DependabotProposalParser
{
	/// <summary>The only author whose pull requests are eligible for triage.</summary>
	public const string DependabotLogin = "dependabot[bot]";

	/// <summary>
	/// What a pull request proposes, or null when it is not a readable Dependabot version bump.
	/// </summary>
	/// <param name="issue">The open item, as the issue list reports it.</param>
	public static DependabotProposal? Parse(RepositoryIssue issue)
	{
		if (!issue.IsPullRequest
			|| !string.Equals(issue.AuthorLogin, DependabotLogin, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		var bumps = BumpsFromBody(issue.Body);

		if (bumps.Count == 0)
		{
			bumps = BumpsFromTitle(issue.Title);
		}

		return bumps.Count == 0
			? null
			: new DependabotProposal(issue.Number, bumps, issue.HtmlUrl);
	}

	/// <summary>
	/// Every dependency move the body states, deduplicated.
	/// </summary>
	/// <remarks>
	/// Dependabot lists a dependency once per manifest it appears in, so the same move can be stated
	/// twice. Left in, it would be written twice and counted twice.
	/// </remarks>
	/// <param name="body">The pull request body, or null when none was fetched.</param>
	private static List<DependabotBump> BumpsFromBody(string? body)
	{
		if (string.IsNullOrWhiteSpace(body))
		{
			return [];
		}

		var seen = new HashSet<(string Name, string To)>();
		var bumps = new List<DependabotBump>();

		foreach (var match in BodyLine().Matches(body).Cast<Match>())
		{
			var name = match.Groups["name"].Value;
			var to = match.Groups["to"].Value;

			if (!seen.Add((name.ToLowerInvariant(), to)))
			{
				continue;
			}

			var directory = match.Groups["dir"];

			bumps.Add(new DependabotBump(
				new DependencyRef(EcosystemOf(name), name),
				match.Groups["from"].Value,
				to,
				directory.Success ? directory.Value : null));
		}

		return bumps;
	}

	/// <summary>
	/// The single move a title states, or none when the title is not a single-dependency bump.
	/// </summary>
	/// <param name="title">The pull request title.</param>
	private static List<DependabotBump> BumpsFromTitle(string title)
	{
		var match = BumpTitle().Match(title);

		if (!match.Success)
		{
			return [];
		}

		var name = match.Groups["name"].Value;
		var directory = match.Groups["dir"];

		return
		[
			new DependabotBump(
				new DependencyRef(EcosystemOf(name), name),
				match.Groups["from"].Value,
				match.Groups["to"].Value,
				directory.Success ? directory.Value : null)
		];
	}

	/// <summary>
	/// A name containing a slash is an <c>owner/name</c> action; anything else is a NuGet package.
	/// </summary>
	/// <remarks>
	/// Inferred rather than declared because the pull request is all there is to go on, and the two
	/// ecosystems this application governs happen to be unambiguous on that one character.
	/// </remarks>
	private static DependencyEcosystem EcosystemOf(string name)
		=> name.Contains('/', StringComparison.Ordinal)
			? DependencyEcosystem.GitHubActions
			: DependencyEcosystem.NuGet;

	/// <summary>
	/// One dependency move as a body states it: <c>Updates `X` from a to b</c> for a grouped pull
	/// request, or <c>Bumps [X](url) from a to b</c> for a single-dependency one.
	/// </summary>
	/// <remarks>
	/// Anchored to the start of a line, so the identical sentences that appear inside the release-notes
	/// and changelog <c>&lt;details&gt;</c> blocks — which quote upstream text describing other
	/// packages entirely — are not read as proposals.
	/// </remarks>
	[GeneratedRegex(
		@"^(?:Updates|Bumps)\s+(?:`(?<name>[^`]+)`|\[(?<name>[^\]]+)\]\([^)]*\)|(?<name>\S+))"
			+ @"\s+from\s+(?<from>\S+)\s+to\s+(?<to>[^\s.]+)\.?(?:\s+in\s+(?<dir>\S+))?\s*$",
		RegexOptions.CultureInvariant | RegexOptions.Multiline)]
	private static partial Regex BodyLine();

	[GeneratedRegex(
		@"^Bump (?<name>\S+) from (?<from>\S+) to (?<to>\S+)(?: in (?<dir>\S+))?$",
		RegexOptions.CultureInvariant)]
	private static partial Regex BumpTitle();
}
```

**Adjust `BodyLine()` to whatever Task 1's fixtures actually show.** The alternation above covers the three forms the spec expected; if the fixtures show a fourth, add it, and if a `to` group swallows a trailing full stop, the `[^\s.]+` class is where to fix it.

- [ ] **Step 5: Fix the two call sites the reshape broke**

`DependabotTriageService.Judge` and `DependabotTriageRunner.RunAsync` both read `proposal.Dependency`, `proposal.FromVersion` and `proposal.ToVersion`. Task 4 rewrites both properly. For **this** task, make them compile against the first bump only, with a comment marking it:

```csharp
		// Task 4 folds this over every bump. Reading only the first keeps the existing single-dependency
		// behaviour exactly as it was while the model reshape lands on its own.
		var bump = proposal.Bumps[0];
```

and replace `proposal.Dependency` with `bump.Dependency`, `proposal.ToVersion` with `bump.ToVersion`, and `proposal.FromVersion` with `bump.FromVersion` throughout both files. Also update every `DependabotTitleParser` reference to `DependabotProposalParser` (there is one in `DependabotTriageService.Judge` and one in the runner's XML docs).

- [ ] **Step 6: Run the tests**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj \
  -p:OutDir="$(pwd)/PanoramicData.NugetManagement.Test/bin/Verify/net10.0/"
./PanoramicData.NugetManagement.Test/bin/Verify/net10.0/PanoramicData.NugetManagement.Test.exe \
  --filter-class "*DependabotProposalParserTests"
./PanoramicData.NugetManagement.Test/bin/Verify/net10.0/PanoramicData.NugetManagement.Test.exe \
  --filter-class "*DependabotTriageServiceTests"
./PanoramicData.NugetManagement.Test/bin/Verify/net10.0/PanoramicData.NugetManagement.Test.exe \
  --filter-class "*DependabotTriageRunnerTests"
```

Expected: PASS for all three. The triage and runner suites are unchanged and must stay green — this task is a reshape, not a behaviour change.

- [ ] **Step 7: Commit**

```bash
git add PanoramicData.NugetManagement/Models/DependabotProposal.cs \
        PanoramicData.NugetManagement/Services/DependabotProposalParser.cs \
        PanoramicData.NugetManagement/Services/DependabotTitleParser.cs \
        PanoramicData.NugetManagement/Services/DependabotTriageService.cs \
        PanoramicData.NugetManagement.Web/Services/DependabotTriageRunner.cs \
        PanoramicData.NugetManagement.Test/DependabotProposalParserTests.cs \
        PanoramicData.NugetManagement.Test/DependabotTitleParserTests.cs
git commit -m "feat: read grouped Dependabot pull requests from their bodies"
```

---

### Task 4: Fold many bumps into one verdict

Triage still reaches one verdict per pull request, but now it reasons over every bump: satisfied ones drop out, and the verdict is the least resolved state across what remains. Gap issues are still raised per bump.

**Files:**
- Modify: `PanoramicData.NugetManagement/Services/DependabotTriageService.cs`
- Modify: `PanoramicData.NugetManagement.Web/Services/DependabotTriageRunner.cs:95-140`
- Test: `PanoramicData.NugetManagement.Test/DependabotTriageServiceTests.cs`

**Interfaces:**
- Consumes: `DependabotProposal.Bumps` (Task 3).
- Produces: `DependabotTriage` gains `IReadOnlyList<DependabotBump> GapBumps` — the bumps that are nobody's job, which the runner raises issues for. Empty unless `IsRuleSetGap`.

- [ ] **Step 1: Write the failing tests**

Add to `DependabotTriageServiceTests`:

```csharp
	/// <summary>
	/// A grouped pull request, with the body Dependabot writes for one.
	/// </summary>
	private static RepositoryIssue GroupedPullRequest(int number, params (string Name, string From, string To)[] bumps)
		=> new()
		{
			Number = number,
			Title = $"Bump the group with {bumps.Length} updates",
			IsPullRequest = true,
			HtmlUrl = $"https://github.com/panoramicdata/Athonet.Api/pull/{number}",
			AuthorLogin = "dependabot[bot]",
			CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
			Body = string.Join(
				"\n",
				bumps.Select(b => $"Updates `{b.Name}` from {b.From} to {b.To}"))
		};

	[Fact]
	public void Triage_GroupWhereEveryBumpIsSatisfied_IsAlreadySatisfied()
	{
		var triages = new DependabotTriageService([]).Triage(
			[GroupedPullRequest(1, ("Serilog", "3.0.0", "4.0.0"), ("Refit", "6.0.0", "7.0.0"))],
			Ctx(Packages("Serilog", "4.1.0"), Packages("Refit", "7.0.0")),
			[],
			_ => true);

		triages[0].Verdict.Should().Be(
			DependabotVerdict.AlreadySatisfied,
			"every dependency it proposes is already declared at or above the target");
	}

	[Fact]
	public void Triage_GroupWhereOneBumpIsOutstanding_IsJudgedOnThatBump()
	{
		var triages = new DependabotTriageService([]).Triage(
			[GroupedPullRequest(1, ("Serilog", "3.0.0", "4.0.0"), ("Refit", "6.0.0", "7.0.0"))],
			Ctx(Packages("Serilog", "4.1.0"), Packages("Refit", "6.0.0")),
			[],
			_ => true);

		triages[0].Verdict.Should().NotBe(
			DependabotVerdict.AlreadySatisfied,
			"one of the two is still outstanding, so the pull request still proposes something");
		triages[0].Reason.Should().Contain(
			"Refit",
			"the reason has to name which dependency drove the verdict, or it sends someone back to "
				+ "GitHub to find out");
	}

	[Fact]
	public void Triage_GroupWithOneUngovernedBump_IsAGapForThatBumpOnly()
	{
		var triages = new DependabotTriageService([new NuGetMajorLevelUpdatesRule()]).Triage(
			[GroupedPullRequest(1, ("Serilog", "3.0.0", "4.0.0"), ("some/action", "1", "2"))],
			Ctx(Packages("Serilog", "3.0.0")),
			[FailingNamingPackages("PKG-07", "Serilog")],
			ruleId => ruleId == "PKG-07");

		triages[0].IsRuleSetGap.Should().BeTrue(
			"no rule governs some/action, so part of this pull request is nobody's job");
		triages[0].GapBumps.Select(b => b.Dependency.Name).Should().BeEquivalentTo(
			["some/action"],
			"the governed half is covered and is not somebody's work — only the ungoverned bump is");
	}
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj \
  -p:OutDir="$(pwd)/PanoramicData.NugetManagement.Test/bin/Verify/net10.0/"
```

Expected: FAIL to compile — `DependabotTriage` has no `GapBumps`.

- [ ] **Step 3: Add `GapBumps` to the verdict record**

In `DependabotTriageService.cs`, add a parameter to `DependabotTriage` after `IsRuleSetGap`:

```csharp
	/// <summary>
	/// The bumps in this pull request that nothing governs, or that are governed by a rule which never
	/// reads where they are declared. Empty unless <see cref="IsRuleSetGap"/>.
	/// </summary>
	/// <remarks>
	/// A grouped pull request can be part covered and part gap, and only the gap half is somebody's
	/// work. Raising an issue for the covered half would be raising an issue against a fix that is
	/// already queued.
	/// </remarks>
	IReadOnlyList<DependabotBump> GapBumps = null!
```

and give it a real default in the record's declaration by making the parameter `IReadOnlyList<DependabotBump>? GapBumps = null`, with the property normalized to empty. The simplest form that keeps the record positional:

```csharp
public sealed record DependabotTriage(
	RepositoryIssue Issue,
	DependabotProposal? Proposal,
	DependabotVerdict Verdict,
	string Reason,
	string? CoveringRuleId,
	bool IsRuleSetGap = false,
	IReadOnlyList<DependabotBump>? GapBumpsOrNull = null)
{
	/// <summary>
	/// The bumps nothing governs. Empty unless <see cref="IsRuleSetGap"/>.
	/// </summary>
	public IReadOnlyList<DependabotBump> GapBumps => GapBumpsOrNull ?? [];
}
```

- [ ] **Step 4: Rewrite `Judge` to fold over the bumps**

Replace `DependabotTriageService.Judge` with:

```csharp
	private DependabotTriage Judge(
		RepositoryIssue issue,
		List<PackageVersionReference> packages,
		List<ActionUsage> actionUsages,
		IReadOnlyList<RuleResult> ruleResults,
		Func<string, bool> canRemediate)
	{
		var proposal = DependabotProposalParser.Parse(issue);

		if (proposal is null)
		{
			return new DependabotTriage(
				issue,
				null,
				DependabotVerdict.Unrecognised,
				"Not a readable Dependabot version bump, so triage leaves it alone.",
				null);
		}

		// Bumps already done drop out: a group of three where two are satisfied should be judged on the
		// one that is not.
		var outstanding = proposal.Bumps
			.Where(bump => !IsSatisfied(bump, packages, actionUsages))
			.ToList();

		if (outstanding.Count == 0)
		{
			return new DependabotTriage(
				issue,
				proposal,
				DependabotVerdict.AlreadySatisfied,
				Describe(proposal.Bumps)
					+ " already declared at the proposed version or above, so merging this would change "
					+ "nothing.",
				null);
		}

		// Covered before anything else: if a failing rule is already going to move a dependency, letting
		// the rule do it keeps one mechanism responsible for one change, and the estate floors keep
		// deciding the target version rather than Dependabot.
		var covering = outstanding
			.Select(bump => CoveringRuleId(bump.Dependency, ruleResults, canRemediate))
			.ToList();

		if (covering.All(ruleId => ruleId is not null))
		{
			return new DependabotTriage(
				issue,
				proposal,
				DependabotVerdict.ValidCovered,
				$"Still outstanding, and {string.Join(", ", covering.Distinct())} failing with a "
					+ $"remediation that will move {Describe(outstanding)} at least this far.",
				covering[0]);
		}

		// Nothing will move some of it today. Whether that is a gap in the rule set or a rule that has
		// nothing to say today is a different question, and only the first is anybody's work.
		var gaps = outstanding
			.Where(bump => IsGap(bump, packages, actionUsages, canRemediate))
			.ToList();

		if (gaps.Count > 0)
		{
			return new DependabotTriage(
				issue,
				proposal,
				DependabotVerdict.ValidUncovered,
				$"Still outstanding, and nothing here can ever move {Describe(gaps)} — no rule governs "
					+ "it, or the rule that claims it never reads where it is declared.",
				null,
				IsRuleSetGap: true,
				GapBumpsOrNull: gaps);
		}

		var idle = outstanding.Where(bump => covering[outstanding.IndexOf(bump)] is null).ToList();

		return new DependabotTriage(
			issue,
			proposal,
			DependabotVerdict.ValidUncovered,
			$"Still outstanding, and {Describe(idle)} governed by a rule that is not failing for it at "
				+ "the moment, so nothing is queued to move it right now.",
			null);
	}

	/// <summary>
	/// Whether nothing here can ever move this bump: no rule governs it, or one claims it but never
	/// reads where it is declared.
	/// </summary>
	private bool IsGap(
		DependabotBump bump,
		List<PackageVersionReference> packages,
		List<ActionUsage> actionUsages,
		Func<string, bool> canRemediate)
		=> GoverningRuleId(bump.Dependency, canRemediate) is null
			|| !IsObserved(bump.Dependency, packages, actionUsages);

	/// <summary>
	/// Names bumps for a sentence a human reads: "Serilog", "Serilog and Refit", "Serilog, Refit and
	/// one other".
	/// </summary>
	/// <remarks>
	/// Every reason sentence names which dependency drove the verdict. A grouped pull request reported
	/// as a gap with no indication of <em>which</em> of its three dependencies is the gap is a sentence
	/// that sends somebody back to GitHub to find out.
	/// </remarks>
	private static string Describe(IReadOnlyList<DependabotBump> bumps)
		=> bumps.Count switch
		{
			0 => "nothing",
			1 => bumps[0].Dependency.Name,
			2 => $"{bumps[0].Dependency.Name} and {bumps[1].Dependency.Name}",
			_ => string.Join(", ", bumps.Take(2).Select(b => b.Dependency.Name))
				+ $" and {bumps.Count - 2} other{(bumps.Count == 3 ? string.Empty : "s")}"
		};
```

Change `IsSatisfied`, `IsPackageSatisfied` and `IsActionSatisfied` to take a `DependabotBump` instead of a `DependabotProposal` — the bodies are unchanged apart from reading `bump.Dependency` and `bump.ToVersion` rather than `proposal.Dependency` and `proposal.ToVersion`.

- [ ] **Step 5: Make the runner raise one sighting per gap bump**

In `DependabotTriageRunner.RunAsync`, the `AlreadySatisfied` case's `resolved` stamp becomes a loop:

```csharp
					if (triage.Proposal is { } satisfied)
					{
						foreach (var bump in satisfied.Bumps)
						{
							resolved[bump.Dependency] =
								$"{repositoryFullName} now declares it at or above the proposed version";
						}
					}
```

the `ValidCovered` case likewise:

```csharp
					if (triage.Proposal is { } covering && triage.CoveringRuleId is { } coveringRuleId)
					{
						foreach (var bump in covering.Bumps)
						{
							resolved[bump.Dependency] =
								$"{coveringRuleId} governs it and its remediation will move it";
						}
					}
```

and the gap case accumulates a sighting per gap bump rather than one per pull request:

```csharp
				case DependabotVerdict.ValidUncovered when triage.Proposal is { } proposal:
					foreach (var bump in triage.GapBumps)
					{
						if (!uncovered.TryGetValue(bump.Dependency, out var sightings))
						{
							sightings = [];
							uncovered[bump.Dependency] = sightings;
						}

						sightings.Add(new UncoveredDependencySighting(
							repositoryFullName,
							proposal.Number,
							bump.FromVersion,
							bump.ToVersion,
							proposal.HtmlUrl));
					}

					break;
```

- [ ] **Step 6: Run the tests**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj \
  -p:OutDir="$(pwd)/PanoramicData.NugetManagement.Test/bin/Verify/net10.0/"
./PanoramicData.NugetManagement.Test/bin/Verify/net10.0/PanoramicData.NugetManagement.Test.exe \
  --filter-class "*DependabotTriageServiceTests"
./PanoramicData.NugetManagement.Test/bin/Verify/net10.0/PanoramicData.NugetManagement.Test.exe \
  --filter-class "*DependabotTriageRunnerTests"
./PanoramicData.NugetManagement.Test/bin/Verify/net10.0/PanoramicData.NugetManagement.Test.exe \
  --filter-class "*TriageRestampTests"
```

Expected: PASS for all three, including the three new folding tests.

- [ ] **Step 7: Commit**

```bash
git add PanoramicData.NugetManagement/Services/DependabotTriageService.cs \
        PanoramicData.NugetManagement.Web/Services/DependabotTriageRunner.cs \
        PanoramicData.NugetManagement.Test/DependabotTriageServiceTests.cs
git commit -m "feat: fold a grouped pull request's bumps into one verdict"
```

---

### Task 5: Extract how a `uses:` line is rewritten

CI-12's `PatternFor` builds the regex that moves an action version. Adoption needs the same regex, and two definitions of it would drift.

**Files:**
- Create: `PanoramicData.NugetManagement/Rules/CiWorkflow/ActionUsesPattern.cs`
- Modify: `PanoramicData.NugetManagement/Rules/CiWorkflow/CiActionVersionFloorRule.cs:224-230` (delete `PatternFor`), `:188-190` (call the extracted helper)
- Test: `PanoramicData.NugetManagement.Test/ActionUsesPatternTests.cs` (create)

**Interfaces:**
- Consumes: `GitHubActionVersion.ParseMajor(string)`, already present.
- Produces:
  - `ActionUsesPattern.Below(string action, string targetSpec) → string` — the regex matching every `uses:` pin below the target.
  - `ActionUsesPattern.Replacement(string targetSpec) → string` — the `${1}vN` replacement.
  - `ActionUsesPattern.WorkflowGlobs` → `string[]`, the two workflow globs.

- [ ] **Step 1: Write the failing test**

Create `PanoramicData.NugetManagement.Test/ActionUsesPatternTests.cs`:

```csharp
using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="ActionUsesPattern"/>: the one definition of how a <c>uses:</c> line is moved
/// to a newer major, shared by CI-12 and by Dependabot adoption.
/// </summary>
public class ActionUsesPatternTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static string Rewrite(string line, string action, string target)
		=> Regex.Replace(
			line,
			ActionUsesPattern.Below(action, target),
			ActionUsesPattern.Replacement(target));

	[Fact]
	public void Below_MatchesAPinBelowTheTarget()
		=> Rewrite("      - uses: actions/checkout@v4", "actions/checkout", "v5")
			.Should().Be("      - uses: actions/checkout@v5");

	[Fact]
	public void Below_LeavesAPinAtTheTargetAlone()
		=> Rewrite("      - uses: actions/checkout@v5", "actions/checkout", "v5")
			.Should().Be("      - uses: actions/checkout@v5");

	[Fact]
	public void Below_LeavesAPinAboveTheTargetAlone()
		=> Rewrite("      - uses: actions/checkout@v6", "actions/checkout", "v5")
			.Should().Be(
				"      - uses: actions/checkout@v6",
				"a repository ahead of the target must never be moved backwards");

	[Fact]
	public void Below_RewritesASubActionToo()
		=> Rewrite("      - uses: github/codeql-action/init@v2", "github/codeql-action", "v4")
			.Should().Be(
				"      - uses: github/codeql-action/init@v4",
				"a sub-action has no version of its own — the repository's version is what is pinned");

	[Fact]
	public void Below_RewritesAPatchQualifiedPin()
		=> Rewrite("      - uses: actions/checkout@v3.1.2", "actions/checkout", "v5")
			.Should().Be("      - uses: actions/checkout@v5");

	[Fact]
	public void Below_LeavesACommitShaPinAlone()
		=> Rewrite(
				"      - uses: actions/checkout@8f4b7f84864484a7bf31766abe9204da3cbe65b3",
				"actions/checkout",
				"v5")
			.Should().Be(
				"      - uses: actions/checkout@8f4b7f84864484a7bf31766abe9204da3cbe65b3",
				"a SHA pin is a deliberate choice and is not a version this can reason about");
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj \
  -p:OutDir="$(pwd)/PanoramicData.NugetManagement.Test/bin/Verify/net10.0/"
```

Expected: FAIL to compile — `ActionUsesPattern` does not exist.

- [ ] **Step 3: Create the helper**

Create `PanoramicData.NugetManagement/Rules/CiWorkflow/ActionUsesPattern.cs`:

```csharp
using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// How a workflow's <c>uses:</c> line is moved to a newer major version.
/// </summary>
/// <remarks>
/// One definition, shared by CI-12 — which moves actions to the version the estate uses elsewhere —
/// and by Dependabot adoption, which moves them to what a pull request proposes. Two copies of this
/// regex would drift, and the failure mode of a drifted version is a rewrite that silently matches
/// nothing.
/// </remarks>
public static class ActionUsesPattern
{
	/// <summary>
	/// The workflow files a rewrite applies to.
	/// </summary>
	public static readonly string[] WorkflowGlobs =
		[".github/workflows/*.yml", ".github/workflows/*.yaml"];

	/// <summary>
	/// A pattern matching every pin of an action <em>below</em> the target major, sub-actions included.
	/// </summary>
	/// <param name="action">The action's repository, as <c>owner/name</c>.</param>
	/// <param name="targetSpec">The target version spec, such as <c>v5</c> or <c>5</c>.</param>
	/// <remarks>
	/// Enumerating the majors below the target, rather than matching any version and comparing, is what
	/// makes the rewrite safe to run over a repository that is already ahead: a pin above the target
	/// simply does not match, so nothing can be moved backwards. The trailing <c>(?![\d.])</c> stops
	/// <c>v1</c> matching the start of <c>v12</c>.
	/// </remarks>
	public static string Below(string action, string targetSpec)
	{
		var target = GitHubActionVersion.ParseMajor(targetSpec);
		var below = string.Join('|', Enumerable.Range(0, target));

		return $@"({Regex.Escape(action)}(?:/[A-Za-z0-9_.-]+)*@)v(?:{below})(?:\.\d+)*(?![\d.])";
	}

	/// <summary>
	/// The replacement for <see cref="Below"/>, keeping the matched <c>owner/name[/sub]@</c> prefix.
	/// </summary>
	/// <param name="targetSpec">The target version spec.</param>
	public static string Replacement(string targetSpec)
		=> $"${{1}}v{GitHubActionVersion.ParseMajor(targetSpec)}";
}
```

Note `Replacement` normalizes to `v{major}`. CI-12 currently emits `${{1}}{floorSpec}` with the raw spec; `ParseMajor` on a spec like `v5` returns `5`, so `v5` comes back out unchanged. Confirm that with Step 5's CI-12 suite.

- [ ] **Step 4: Point CI-12 at it**

In `CiActionVersionFloorRule.cs`, delete the private `PatternFor` method and the `_workflowGlobs` field, and change the advisory data to:

```csharp
					["globs"] = ActionUsesPattern.WorkflowGlobs,
					["patterns"] = behind.Select(b => ActionUsesPattern.Below(b.Action, b.Floor)).ToArray(),
					["replacements"] = behind.Select(b => ActionUsesPattern.Replacement(b.Floor)).ToArray(),
```

Remove the now-unused `using System.Text.RegularExpressions;` only if nothing else in the file needs it.

- [ ] **Step 5: Run the tests**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj \
  -p:OutDir="$(pwd)/PanoramicData.NugetManagement.Test/bin/Verify/net10.0/"
./PanoramicData.NugetManagement.Test/bin/Verify/net10.0/PanoramicData.NugetManagement.Test.exe \
  --filter-class "*ActionUsesPatternTests"
./PanoramicData.NugetManagement.Test/bin/Verify/net10.0/PanoramicData.NugetManagement.Test.exe \
  --filter-class "*CiActionVersionFloorRuleTests"
```

Expected: PASS for both. CI-12's existing suite is the regression check for the extraction — if it fails, `Replacement` is not reproducing what the rule emitted before.

- [ ] **Step 6: Commit**

```bash
git add PanoramicData.NugetManagement/Rules/CiWorkflow/ActionUsesPattern.cs \
        PanoramicData.NugetManagement/Rules/CiWorkflow/CiActionVersionFloorRule.cs \
        PanoramicData.NugetManagement.Test/ActionUsesPatternTests.cs
git commit -m "refactor: one definition of how a uses: line is moved to a newer major"
```

---

### Task 6: The `Adoptable` verdict, the age gate, and the adoption plan

Triage learns to say "nothing is failing for this, but the pull request is old enough and we can write it" — and computes exactly what would be written. Still pure: no files are touched in this task.

**Files:**
- Modify: `PanoramicData.NugetManagement/Models/DependabotVerdict.cs`
- Create: `PanoramicData.NugetManagement/Models/DependabotAdoptionPlan.cs`
- Modify: `PanoramicData.NugetManagement/Services/DependabotTriageService.cs`
- Test: `PanoramicData.NugetManagement.Test/DependabotAdoptionPlanTests.cs` (create)

**Interfaces:**
- Consumes: `ActionUsesPattern` (Task 5), `DependabotTriage` (Task 4), `PackageVersionReference`, `ActionUsage`.
- Produces:
  - `DependabotVerdict.Adoptable` — **appended last**.
  - `record DependabotAdoptionPlan(IReadOnlyList<string> PackageUpdates, IReadOnlyList<string> ActionPatterns, IReadOnlyList<string> ActionReplacements)` with `bool HasAnything`.
  - `DependabotTriage.Adoption` (`DependabotAdoptionPlan?`).
  - `DependabotTriageService.AdoptAfter` (`static readonly TimeSpan`, 60 days).
  - `DependabotTriageService(IReadOnlyList<IRule> rules, TimeProvider timeProvider)`.

- [ ] **Step 1: Append the verdict and create the plan record**

In `DependabotVerdict.cs`, add **after** `ValidUncovered` — last, because the enum is persisted as a JSON number and inserting mid-enum rewrites the meaning of every cached verdict:

```csharp
	,

	/// <summary>
	/// Still worth doing, nothing is failing for it, and the pull request has been open long enough
	/// that no grace period is still protecting anything — so the bump is written to the local clone
	/// and the pull request closed.
	/// </summary>
	/// <remarks>
	/// Named for the state triage found rather than the act the runner performs, as
	/// <see cref="ValidCovered"/> is. Triage decides; the runner adopts, and only if the write applies
	/// something.
	/// </remarks>
	Adoptable
```

Create `PanoramicData.NugetManagement/Models/DependabotAdoptionPlan.cs`:

```csharp
namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// Exactly what adopting one pull request would write, in the form the existing remediations read.
/// </summary>
/// <param name="PackageUpdates">
/// One pipe-delimited record per package declaration to rewrite:
/// <c>filePath|packageId|versionKind|currentVersion|targetVersion</c>, as
/// <c>update_package_versions</c> parses.
/// </param>
/// <param name="ActionPatterns">Regexes matching the action pins to move.</param>
/// <param name="ActionReplacements">
/// The replacement for each pattern, positionally. Always the same length as
/// <paramref name="ActionPatterns"/>, because <c>replace_regex_in_files</c> pairs them by index.
/// </param>
/// <remarks>
/// Computed by triage rather than by whatever applies it, because everything needed to compute it —
/// what the repository declares and where — is already in hand there, and because a plan is then a
/// pure value a test can assert on without a clone, a network or a working tree.
/// </remarks>
public sealed record DependabotAdoptionPlan(
	IReadOnlyList<string> PackageUpdates,
	IReadOnlyList<string> ActionPatterns,
	IReadOnlyList<string> ActionReplacements)
{
	/// <summary>Whether this plan would write anything at all.</summary>
	public bool HasAnything => PackageUpdates.Count > 0 || ActionPatterns.Count > 0;
}
```

- [ ] **Step 2: Write the failing tests**

Create `PanoramicData.NugetManagement.Test/DependabotAdoptionPlanTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the <see cref="DependabotVerdict.Adoptable"/> verdict: when a pull request nothing is
/// failing for becomes old enough to act on anyway, and exactly what would then be written.
/// </summary>
public class DependabotAdoptionPlanTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly DateTimeOffset _raised = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private const string _packagesProps = "Directory.Packages.props";
	private const string _ciPath = ".github/workflows/ci.yml";

	private static RepositoryIssue PullRequest(string body, DateTimeOffset? raisedAt = null) => new()
	{
		Number = 6,
		Title = "Bump the group",
		IsPullRequest = true,
		HtmlUrl = "https://github.com/panoramicdata/Highlight.Api/pull/6",
		AuthorLogin = "dependabot[bot]",
		CreatedAtUtc = raisedAt ?? _raised,
		Body = body
	};

	private static RepositoryContext Ctx(params (string Path, string Content)[] files) => new()
	{
		FullName = "panoramicdata/Highlight.Api",
		Name = "Highlight.Api",
		DefaultBranch = "main",
		CurrentBranch = "main",
		Options = new RepoOptions(),
		FilePaths = [.. files.Select(f => f.Path)],
		FileContents = files.ToDictionary(f => f.Path, f => f.Content, StringComparer.OrdinalIgnoreCase)
	};

	private static (string, string) Packages(string packageId, string version)
		=> (_packagesProps,
			$"""<Project><ItemGroup><PackageVersion Include="{packageId}" Version="{version}" /></ItemGroup></Project>""");

	private static (string, string) Workflow(params string[] uses)
		=> (_ciPath, "jobs:\n  build:\n    steps:\n" + string.Concat(uses.Select(u => $"    - uses: {u}\n")));

	/// <summary>
	/// Triage as of a given age for the pull request, over the real package rules — which govern every
	/// NuGet package and, being passed no failing results, are failing for nothing.
	/// </summary>
	private static DependabotTriage JudgeAt(
		RepositoryIssue issue,
		RepositoryContext context,
		TimeSpan age)
		=> new DependabotTriageService(
				[new NuGetMajorLevelUpdatesRule(), new CiActionVersionFloorRule()],
				new FakeTimeProvider(issue.CreatedAtUtc + age))
			.Triage([issue], context, [], _ => true)[0];

	[Fact]
	public void Triage_PastTheThreshold_IsAdoptable()
		=> JudgeAt(
				PullRequest("Updates `coverlet.collector` from 8.0.1 to 10.0.0"),
				Ctx(Packages("coverlet.collector", "8.0.1")),
				DependabotTriageService.AdoptAfter + TimeSpan.FromDays(1))
			.Verdict.Should().Be(
				DependabotVerdict.Adoptable,
				"nothing is failing for it, but it has been open long enough that no grace period is "
					+ "still protecting anything");

	[Fact]
	public void Triage_OneDayShortOfTheThreshold_IsNotAdoptable()
		=> JudgeAt(
				PullRequest("Updates `coverlet.collector` from 8.0.1 to 10.0.0"),
				Ctx(Packages("coverlet.collector", "8.0.1")),
				DependabotTriageService.AdoptAfter - TimeSpan.FromDays(1))
			.Verdict.Should().Be(
				DependabotVerdict.ValidUncovered,
				"inside the threshold the grace periods are still doing their job, and adoption must "
					+ "not pre-empt them");

	[Fact]
	public void Triage_Adoptable_PlansThePackageRewrite()
	{
		var plan = JudgeAt(
			PullRequest("Updates `coverlet.collector` from 8.0.1 to 10.0.0"),
			Ctx(Packages("coverlet.collector", "8.0.1")),
			DependabotTriageService.AdoptAfter + TimeSpan.FromDays(1)).Adoption;

		plan.Should().NotBeNull();
		plan!.PackageUpdates.Should().BeEquivalentTo(
			[$"{_packagesProps}|coverlet.collector|PackageVersionAttribute|8.0.1|10.0.0"],
			"update_package_versions parses file|package|kind|from|to, and the target is what the pull "
				+ "request proposed");
		plan.ActionPatterns.Should().BeEmpty();
	}

	[Fact]
	public void Triage_AdoptableAction_PlansTheWorkflowRewrite()
	{
		var plan = JudgeAt(
			PullRequest("Updates `actions/checkout` from 4 to 5"),
			Ctx(Workflow("actions/checkout@v4")),
			DependabotTriageService.AdoptAfter + TimeSpan.FromDays(1)).Adoption;

		plan.Should().NotBeNull();
		plan!.ActionPatterns.Should().BeEquivalentTo([ActionUsesPattern.Below("actions/checkout", "5")]);
		plan.ActionReplacements.Should().BeEquivalentTo([ActionUsesPattern.Replacement("5")]);
		plan.PackageUpdates.Should().BeEmpty();
	}

	[Fact]
	public void Triage_GroupWithOneUnwritableBump_IsNotAdoptable()
		=> JudgeAt(
				PullRequest("""
					Updates `coverlet.collector` from 8.0.1 to 10.0.0
					Updates `Serilog` from 3.0.0 to 4.0.0
					"""),
				Ctx(Packages("coverlet.collector", "8.0.1")),
				DependabotTriageService.AdoptAfter + TimeSpan.FromDays(1))
			.Verdict.Should().NotBe(
				DependabotVerdict.Adoptable,
				"Serilog is declared nowhere the scanner reads, so adopting part of the group and "
					+ "closing it would silently drop the rest");

	[Fact]
	public void Triage_AlreadySatisfiedPastTheThreshold_IsStillAlreadySatisfied()
		=> JudgeAt(
				PullRequest("Updates `coverlet.collector` from 8.0.1 to 10.0.0"),
				Ctx(Packages("coverlet.collector", "10.0.0")),
				DependabotTriageService.AdoptAfter + TimeSpan.FromDays(1))
			.Verdict.Should().Be(
				DependabotVerdict.AlreadySatisfied,
				"age never turns a redundant pull request into one worth writing");
}
```

The `PackageVersionAttribute` token in the expected `PackageUpdates` string is `PackageVersionReference.VersionKind`: the scanner emits `$"{elementName}Attribute"` or `$"{elementName}Element"`, so a `<PackageVersion Include="..." Version="..." />` in `Directory.Packages.props` yields `PackageVersionAttribute`, and a `<PackageReference>` with a nested `<Version>` element yields `PackageReferenceElement`. **If the assertion fails on that token, read what `PackageReferenceScanner` actually emits and use it** — do not change the scanner, and do not hand-build the string anywhere in production code: `Plan` copies `d.VersionKind` straight through.

- [ ] **Step 3: Run to verify they fail**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj \
  -p:OutDir="$(pwd)/PanoramicData.NugetManagement.Test/bin/Verify/net10.0/"
```

Expected: FAIL to compile — no `AdoptAfter`, no two-argument constructor, no `Adoption` property.

- [ ] **Step 4: Add the clock, the gate and the plan**

In `DependabotTriageService.cs`:

Add `Adoption` to the `DependabotTriage` record, after `GapBumpsOrNull`:

```csharp
	/// <summary>
	/// What adopting this pull request would write, or null when it is not
	/// <see cref="DependabotVerdict.Adoptable"/>.
	/// </summary>
	DependabotAdoptionPlan? Adoption = null
```

Correct the class-level `<remarks>`, which currently promises "no I/O, no GitHub, no clock" — it now has a clock:

```csharp
/// Pure apart from a clock: no I/O and no GitHub. Everything else it needs is the pull requests, what
/// the repository declares, which rules are failing, and whether a rule has a remediation. The clock
/// is needed only for the adoption age gate, and arrives as a <see cref="TimeProvider"/> so a test can
/// stand either side of the threshold.
```

Add the threshold and the constructor:

```csharp
	/// <summary>
	/// How long a still-valid pull request may stay open before its bump is adopted regardless of
	/// whether any rule is failing for it.
	/// </summary>
	/// <remarks>
	/// Sixty days sits above PKG-05's 30-day build grace and below PKG-06's 90-day minor grace. It is
	/// not trying to mirror the graces — it is a backstop against a pull request rotting, and its only
	/// job is to be long enough that nothing is adopted while a grace period is still doing useful
	/// work.
	/// <para>
	/// A constant rather than a setting. One number nobody has asked to change is not worth a settings
	/// row — and a new <c>RuntimeSettings</c> property has to be added to the hand-written
	/// <c>SaveToDisk</c> snapshot or every save silently erases it.
	/// </para>
	/// </remarks>
	public static readonly TimeSpan AdoptAfter = TimeSpan.FromDays(60);

	private readonly TimeProvider _timeProvider;

	/// <summary>
	/// Initializes a new instance over an explicit rule set and clock, for tests.
	/// </summary>
	/// <param name="rules">The rules to consider when deciding coverage.</param>
	/// <param name="timeProvider">The clock the adoption age gate is measured against.</param>
	public DependabotTriageService(IReadOnlyList<IRule> rules, TimeProvider timeProvider)
	{
		_rules = rules;
		_timeProvider = timeProvider;
	}
```

and make the two existing constructors chain to it: `public DependabotTriageService() : this(RuleRegistry.Rules, TimeProvider.System) { }` and `public DependabotTriageService(IReadOnlyList<IRule> rules) : this(rules, TimeProvider.System) { }`. Delete the old `_rules` field assignment from the one-argument constructor.

In `Judge`, insert the adoption check **between** the covered check and the gap check:

```csharp
		// Nothing failing will move all of it. Before calling that a gap, ask whether the pull request
		// has simply been waiting too long: a grace period exists to avoid churning on a release
		// published this morning, not to hold a pull request open for four months.
		if (_timeProvider.GetUtcNow() - issue.CreatedAtUtc >= AdoptAfter
			&& Plan(outstanding, packages, actionUsages) is { HasAnything: true } plan)
		{
			return new DependabotTriage(
				issue,
				proposal,
				DependabotVerdict.Adoptable,
				$"Open for {(_timeProvider.GetUtcNow() - issue.CreatedAtUtc).Days} days with nothing "
					+ $"queued to move it, so adopting what it proposes for {Describe(outstanding)}.",
				null,
				Adoption: plan);
		}
```

and add the plan builder:

```csharp
	/// <summary>
	/// What writing every one of these bumps would take, or null when any of them cannot be written.
	/// </summary>
	/// <remarks>
	/// All-or-nothing, deliberately. A pull request is adopted only when every outstanding bump in it
	/// can be written: adopting part of a group and closing it silently drops the rest, and adopting
	/// part without closing leaves a pull request whose content is mostly already applied — noise on
	/// every subsequent pass. "Closed" has to keep meaning "fully superseded".
	/// </remarks>
	private static DependabotAdoptionPlan? Plan(
		IReadOnlyList<DependabotBump> bumps,
		List<PackageVersionReference> packages,
		List<ActionUsage> actionUsages)
	{
		var packageUpdates = new List<string>();
		var patterns = new List<string>();
		var replacements = new List<string>();

		foreach (var bump in bumps)
		{
			switch (bump.Dependency.Ecosystem)
			{
				case DependencyEcosystem.NuGet:
					if (!NuGetVersion.TryParse(bump.ToVersion, out _))
					{
						return null;
					}

					var declarations = packages
						.Where(p => string.Equals(
							p.PackageId, bump.Dependency.Name, StringComparison.OrdinalIgnoreCase))
						.ToList();

					if (declarations.Count == 0)
					{
						return null;
					}

					packageUpdates.AddRange(declarations.Select(d => string.Join(
						'|',
						d.FilePath,
						d.PackageId,
						d.VersionKind,
						d.CurrentVersion,
						bump.ToVersion)));

					break;

				case DependencyEcosystem.GitHubActions:
					if (MajorOf(bump.ToVersion) is null
						|| !actionUsages.Any(u => string.Equals(
							u.Action, bump.Dependency.Name, StringComparison.OrdinalIgnoreCase)))
					{
						return null;
					}

					patterns.Add(ActionUsesPattern.Below(bump.Dependency.Name, bump.ToVersion));
					replacements.Add(ActionUsesPattern.Replacement(bump.ToVersion));

					break;

				default:
					return null;
			}
		}

		return new DependabotAdoptionPlan(packageUpdates, patterns, replacements);
	}
```

- [ ] **Step 5: Run the tests**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj \
  -p:OutDir="$(pwd)/PanoramicData.NugetManagement.Test/bin/Verify/net10.0/"
./PanoramicData.NugetManagement.Test/bin/Verify/net10.0/PanoramicData.NugetManagement.Test.exe \
  --filter-class "*DependabotAdoptionPlanTests"
./PanoramicData.NugetManagement.Test/bin/Verify/net10.0/PanoramicData.NugetManagement.Test.exe \
  --filter-class "*DependabotTriageServiceTests"
```

Expected: PASS for both. The existing triage suite builds its service with the one-argument constructor and pull requests dated 2026-01-01, so under the real clock they are well past the threshold — **if any of those tests now report `Adoptable` where they expected `ValidUncovered`, that is the age gate working, and the fix is to give those tests a `FakeTimeProvider` pinned near the pull request's creation date**, not to weaken the gate.

- [ ] **Step 6: Commit**

```bash
git add PanoramicData.NugetManagement/Models/DependabotVerdict.cs \
        PanoramicData.NugetManagement/Models/DependabotAdoptionPlan.cs \
        PanoramicData.NugetManagement/Services/DependabotTriageService.cs \
        PanoramicData.NugetManagement.Test/DependabotAdoptionPlanTests.cs \
        PanoramicData.NugetManagement.Test/DependabotTriageServiceTests.cs
git commit -m "feat: adopt a bump nothing is failing for once its pull request is 60 days old"
```

---

### Task 7: Apply an adoption plan

The plan becomes files on disk, through the two writers that already exist. This is the first task that touches a working tree.

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Remediations/DependabotAdoptionRemediation.cs`
- Test: `PanoramicData.NugetManagement.Test/DependabotAdoptionRemediationTests.cs` (create)

**Interfaces:**
- Consumes: `DependabotAdoptionPlan` (Task 6), `DataDrivenRemediation`, `RemediationHelpers`.
- Produces: `DependabotAdoptionRemediation.Adopt(string localPath, DependabotAdoptionPlan plan, Action<string>? onOutput) → IReadOnlyList<string>` — the files actually written, empty when nothing was.

- [ ] **Step 1: Write the failing test**

Create `PanoramicData.NugetManagement.Test/DependabotAdoptionRemediationTests.cs`:

```csharp
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="DependabotAdoptionRemediation"/>: it turns an adoption plan into file
/// rewrites, and reports what it wrote so the runner knows whether closing the pull request is
/// honest.
/// </summary>
public class DependabotAdoptionRemediationTests(ITestOutputHelper output) : TestWithOutput(output)
{
	/// <summary>A throwaway working tree, seeded with the given files.</summary>
	private static string Tree(params (string RelativePath, string Content)[] files)
	{
		var root = Path.Combine(Path.GetTempPath(), "adopt-" + Guid.NewGuid().ToString("N"));

		foreach (var (relativePath, content) in files)
		{
			var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(full)!);
			File.WriteAllText(full, content);
		}

		return root;
	}

	[Fact]
	public void Adopt_APackageUpdate_RewritesTheDeclaredVersion()
	{
		var root = Tree(("Directory.Packages.props", """
			<Project><ItemGroup><PackageVersion Include="coverlet.collector" Version="8.0.1" /></ItemGroup></Project>
			"""));

		var written = DependabotAdoptionRemediation.Adopt(
			root,
			new DependabotAdoptionPlan(
				["Directory.Packages.props|coverlet.collector|PackageVersionAttribute|8.0.1|10.0.0"],
				[],
				[]),
			Output.WriteLine);

		written.Should().NotBeEmpty("the file was there to write");
		File.ReadAllText(Path.Combine(root, "Directory.Packages.props"))
			.Should().Contain(@"Version=""10.0.0""").And.NotContain(@"Version=""8.0.1""");

		Directory.Delete(root, recursive: true);
	}

	[Fact]
	public void Adopt_AnActionUpdate_RewritesEveryUsesLine()
	{
		var root = Tree((".github/workflows/ci.yml", """
			jobs:
			  build:
			    steps:
			    - uses: actions/checkout@v4
			    - uses: actions/checkout@v4
			"""));

		var written = DependabotAdoptionRemediation.Adopt(
			root,
			new DependabotAdoptionPlan(
				[],
				[ActionUsesPattern.Below("actions/checkout", "5")],
				[ActionUsesPattern.Replacement("5")]),
			Output.WriteLine);

		written.Should().NotBeEmpty();
		File.ReadAllText(Path.Combine(root, ".github/workflows/ci.yml"))
			.Should().NotContain("actions/checkout@v4")
			.And.Contain("actions/checkout@v5");

		Directory.Delete(root, recursive: true);
	}

	[Fact]
	public void Adopt_WhenTheFileIsNotThere_WritesNothingAndSaysSo()
	{
		var root = Tree(("README.md", "nothing to rewrite here"));

		DependabotAdoptionRemediation
			.Adopt(
				root,
				new DependabotAdoptionPlan(
					["Directory.Packages.props|coverlet.collector|PackageVersionAttribute|8.0.1|10.0.0"],
					[],
					[]),
				Output.WriteLine)
			.Should().BeEmpty(
				"reporting nothing written is what stops the runner closing a pull request against no "
					+ "change at all");

		Directory.Delete(root, recursive: true);
	}

	[Fact]
	public void Adopt_APlanWithBothKinds_AppliesBoth()
	{
		var root = Tree(
			("Directory.Packages.props", """
				<Project><ItemGroup><PackageVersion Include="Serilog" Version="3.0.0" /></ItemGroup></Project>
				"""),
			(".github/workflows/ci.yml", "jobs:\n  build:\n    steps:\n    - uses: actions/checkout@v4\n"));

		var written = DependabotAdoptionRemediation.Adopt(
			root,
			new DependabotAdoptionPlan(
				["Directory.Packages.props|Serilog|PackageVersionAttribute|3.0.0|4.0.0"],
				[ActionUsesPattern.Below("actions/checkout", "5")],
				[ActionUsesPattern.Replacement("5")]),
			Output.WriteLine);

		written.Should().HaveCountGreaterThanOrEqualTo(2, "both halves of the plan had work to do");

		Directory.Delete(root, recursive: true);
	}
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj \
  -p:OutDir="$(pwd)/PanoramicData.NugetManagement.Test/bin/Verify/net10.0/"
```

Expected: FAIL to compile — `DependabotAdoptionRemediation` does not exist.

- [ ] **Step 3: Write the remediation**

Create `PanoramicData.NugetManagement.Web/Remediations/DependabotAdoptionRemediation.cs`:

```csharp
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Web.Remediations;

/// <summary>
/// Writes what a Dependabot pull request proposed, through the remediation writers that already
/// exist.
/// </summary>
/// <remarks>
/// A fix whose source is an open pull request rather than a failing rule. Everything about the write
/// itself is unchanged: <c>update_package_versions</c> and <c>replace_regex_in_files</c> do the work,
/// so there is one implementation of "move a package version" and one of "move an action pin", and
/// adoption cannot drift from what the rules do.
/// <para>
/// Deliberately <em>not</em> registered in <c>RemediationRegistry</c>. That registry is keyed by rule
/// id and triage's own coverage predicate reads it — registering this under a pretend rule id would
/// have it answer "yes, a remediation exists" for a rule that does not exist, and would surface in the
/// registry-coverage and self-assessment suites as a rule with no rule.
/// </para>
/// </remarks>
public sealed class DependabotAdoptionRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	/// <remarks>
	/// No rule owns this. The value exists because <see cref="IRemediation"/> requires one, and names
	/// what it is rather than pretending to be a rule id, so anything that did try to key on it fails
	/// loudly rather than matching a real rule.
	/// </remarks>
	public override string RuleId => "dependabot-adoption";

	/// <summary>
	/// Applies an adoption plan to a clone, and reports the files it wrote.
	/// </summary>
	/// <param name="localPath">The root of the cloned repository.</param>
	/// <param name="plan">What to write.</param>
	/// <param name="onOutput">Where progress is announced.</param>
	/// <returns>
	/// The files actually rewritten. Empty means nothing was written — which is the answer the caller
	/// needs before it closes anybody's pull request, because a writer that matched nothing looks
	/// exactly like a writer that succeeded.
	/// </returns>
	public static IReadOnlyList<string> Adopt(
		string localPath,
		DependabotAdoptionPlan plan,
		Action<string>? onOutput)
	{
		var remediation = new DependabotAdoptionRemediation();
		var applied = new List<string>();

		if (plan.PackageUpdates.Count > 0)
		{
			remediation.Apply(
				localPath,
				Synthesize(new()
				{
					["remediation_type"] = "update_package_versions",
					["updates"] = plan.PackageUpdates.ToArray()
				}),
				applied,
				onOutput);
		}

		if (plan.ActionPatterns.Count > 0)
		{
			remediation.Apply(
				localPath,
				Synthesize(new()
				{
					["remediation_type"] = "replace_regex_in_files",
					["globs"] = ActionUsesPattern.WorkflowGlobs,
					["patterns"] = plan.ActionPatterns.ToArray(),
					["replacements"] = plan.ActionReplacements.ToArray()
				}),
				applied,
				onOutput);
		}

		return applied;
	}

	/// <summary>
	/// A failing result carrying the given advisory data, and nothing else worth reading.
	/// </summary>
	/// <remarks>
	/// <see cref="IRemediation.Apply"/> reads nothing from a result except <c>Advisory.Data</c>, which
	/// is what lets adoption reuse the writers without a rule behind it or a new interface. The other
	/// properties are required by the model, so they say what this is rather than imitating a rule.
	/// </remarks>
	/// <param name="data">The advisory data the writer will read.</param>
	private static RuleResult Synthesize(Dictionary<string, object> data) => new()
	{
		RuleId = "dependabot-adoption",
		RuleName = "Adopting what a Dependabot pull request proposed",
		Category = AssessmentCategory.NuGetHygiene,
		Severity = AssessmentSeverity.Warning,
		Passed = false,
		Message = "Adopting what a Dependabot pull request proposed.",
		Advisory = new RuleAdvisory
		{
			Summary = "Write the versions the pull request proposed.",
			Detail = "Applied by Dependabot adoption rather than by a failing rule.",
			Data = data
		}
	};
}
```

- [ ] **Step 4: Run the tests**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj \
  -p:OutDir="$(pwd)/PanoramicData.NugetManagement.Test/bin/Verify/net10.0/"
./PanoramicData.NugetManagement.Test/bin/Verify/net10.0/PanoramicData.NugetManagement.Test.exe \
  --filter-class "*DependabotAdoptionRemediationTests"
```

Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Remediations/DependabotAdoptionRemediation.cs \
        PanoramicData.NugetManagement.Test/DependabotAdoptionRemediationTests.cs
git commit -m "feat: apply an adoption plan through the existing remediation writers"
```

---

### Task 8: Adopt, then close — but only if the write happened

The runner learns the new verdict. It stays free of file I/O: adoption arrives as a port, so the runner is still testable with no clone and no network.

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Services/IBumpAdopter.cs`
- Modify: `PanoramicData.NugetManagement.Web/Services/DependabotTriageRunner.cs`
- Test: `PanoramicData.NugetManagement.Test/DependabotTriageRunnerTests.cs`

**Interfaces:**
- Consumes: `DependabotTriage.Adoption` (Task 6), `DependabotAdoptionRemediation.Adopt` (Task 7).
- Produces:
  - `interface IBumpAdopter { IReadOnlyList<string> Adopt(DependabotAdoptionPlan plan, Action<string> onOutput); }`
  - `DependabotTriageRunner.AdoptedMarker` = `"<!-- nugetmgmt:closed:adopted -->"`.
  - `DependabotTriageOutcome` gains `int Adopted` — **appended last**, after `Unrecognised`.
  - `RunAsync` gains a final parameter `IBumpAdopter? adopter = null`.

- [ ] **Step 1: Write the failing tests**

Add to `DependabotTriageRunnerTests`:

```csharp
	/// <summary>An adopter that records the plans handed to it and reports a fixed result.</summary>
	private sealed class StubAdopter(params string[] written) : IBumpAdopter
	{
		public List<DependabotAdoptionPlan> Plans { get; } = [];

		public IReadOnlyList<string> Adopt(DependabotAdoptionPlan plan, Action<string> onOutput)
		{
			Plans.Add(plan);
			onOutput("stub adopter ran");
			return written;
		}
	}

	private static DependabotTriage Adoptable(int number) => new(
		new RepositoryIssue
		{
			Number = number,
			Title = "Bump coverlet.collector from 8.0.1 to 10.0.0",
			IsPullRequest = true,
			HtmlUrl = $"https://github.com/panoramicdata/Highlight.Api/pull/{number}",
			AuthorLogin = "dependabot[bot]",
			CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
		},
		new DependabotProposal(
			number,
			[
				new DependabotBump(
					new DependencyRef(DependencyEcosystem.NuGet, "coverlet.collector"),
					"8.0.1",
					"10.0.0",
					null)
			],
			$"https://github.com/panoramicdata/Highlight.Api/pull/{number}"),
		DependabotVerdict.Adoptable,
		"Open for 138 days with nothing queued to move it, so adopting what it proposes.",
		null,
		Adoption: new DependabotAdoptionPlan(
			["Directory.Packages.props|coverlet.collector|PackageVersionAttribute|8.0.1|10.0.0"],
			[],
			[]));

	[Fact]
	public async Task RunAsync_Adoptable_WritesThenCommentsThenCloses()
	{
		var writeApi = new RecordingWriteApi();
		var adopter = new StubAdopter("Directory.Packages.props");

		var outcome = await new DependabotTriageRunner(UncoveredIssues()).RunAsync(
			new NoOpenIssues(),
			writeApi,
			_repository,
			[Adoptable(6)],
			_ => { },
			CancellationToken.None,
			adopter);

		outcome.Adopted.Should().Be(1);
		adopter.Plans.Should().HaveCount(1);
		writeApi.Calls.Should().Equal(
			["comment:6", "close:6"],
			"the explanation lands before the close, so a human who finds it closed has something to "
				+ "read");
		writeApi.Comments[0].Body.Should().Contain(
			DependabotTriageRunner.AdoptedMarker,
			"the two reasons for a close have to stay distinguishable in a pull request's history");
	}

	[Fact]
	public async Task RunAsync_AdoptableButNothingWritten_DoesNotClose()
	{
		var writeApi = new RecordingWriteApi();

		var outcome = await new DependabotTriageRunner(UncoveredIssues()).RunAsync(
			new NoOpenIssues(),
			writeApi,
			_repository,
			[Adoptable(6)],
			_ => { },
			CancellationToken.None,
			new StubAdopter());

		outcome.Adopted.Should().Be(0);
		writeApi.Calls.Should().BeEmpty(
			"a writer that matched nothing looks exactly like one that succeeded — closing on that "
				+ "basis would close a pull request against no change at all");
	}

	[Fact]
	public async Task RunAsync_AdoptableWithNoAdopter_ClosesNothing()
	{
		var writeApi = new RecordingWriteApi();

		var outcome = await new DependabotTriageRunner(UncoveredIssues()).RunAsync(
			new NoOpenIssues(),
			writeApi,
			_repository,
			[Adoptable(6)],
			_ => { },
			CancellationToken.None);

		outcome.Adopted.Should().Be(0);
		writeApi.Calls.Should().BeEmpty("with no clone to write to, nothing can be adopted");
	}
```

Reuse the existing suite's helper for constructing the runner's `UncoveredDependencyIssueService`; if it is built inline in the existing tests, extract it to a `private static UncoveredDependencyIssueService UncoveredIssues()` helper as part of this step so the new tests read the same way.

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj \
  -p:OutDir="$(pwd)/PanoramicData.NugetManagement.Test/bin/Verify/net10.0/"
```

Expected: FAIL to compile — no `IBumpAdopter`, no `AdoptedMarker`, no `Adopted` on the outcome, and `RunAsync` takes six arguments.

- [ ] **Step 3: Create the port**

Create `PanoramicData.NugetManagement.Web/Services/IBumpAdopter.cs`:

```csharp
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Writes an adoption plan to a repository's local clone.
/// </summary>
/// <remarks>
/// A port so <see cref="DependabotTriageRunner"/> can stay what its documentation claims: a component
/// whose writes are all to GitHub, testable with no clone, no working tree and no network. The runner
/// decides whether to close a pull request from what this reports, and nothing else.
/// </remarks>
public interface IBumpAdopter
{
	/// <summary>
	/// Writes the plan, and reports the files it changed.
	/// </summary>
	/// <param name="plan">What to write.</param>
	/// <param name="onOutput">Where progress is announced.</param>
	/// <returns>The files changed. Empty means nothing was written.</returns>
	IReadOnlyList<string> Adopt(DependabotAdoptionPlan plan, Action<string> onOutput);
}
```

- [ ] **Step 4: Teach the runner the verdict**

In `DependabotTriageRunner.cs`:

Add the marker beside `ClosedMarker`:

```csharp
	/// <summary>
	/// The hidden marker on a comment closing a pull request whose bump this application has adopted
	/// into the local clone.
	/// </summary>
	/// <remarks>
	/// Separate from <see cref="ClosedMarker"/> because the two are different claims: one says the
	/// repository had already outgrown the pull request, the other says this application has just
	/// written what the pull request proposed. Someone reading the history needs to be able to tell
	/// them apart.
	/// </remarks>
	public const string AdoptedMarker = "<!-- nugetmgmt:closed:adopted -->";
```

Add `int Adopted` as the **last** parameter of `DependabotTriageOutcome`, with its doc comment:

```csharp
/// <param name="Adopted">
/// Pull requests whose bumps were written to the local clone and which were then closed. Counts what
/// was actually written, not what was found adoptable.
/// </param>
```

Add the optional adopter parameter to `RunAsync`, after `cancellationToken`:

```csharp
		CancellationToken cancellationToken,
		IBumpAdopter? adopter = null)
```

with the doc comment:

```csharp
	/// <param name="adopter">
	/// Writes an adoption plan to the local clone, or null when there is no clone to write to — in
	/// which case nothing is adopted and no pull request is closed on that basis.
	/// </param>
```

Add `var adopted = 0;` beside the other counters, and a case **before** the `ValidUncovered` cases:

```csharp
				case DependabotVerdict.Adoptable when triage.Adoption is { } plan:
					if (adopter is null)
					{
						idle++;
						onOutput(
							$"↺ #{triage.Issue.Number} left open: adoptable, but there is no local clone "
							+ "to write the bump to.");
						break;
					}

					onOutput($"🔧 #{triage.Issue.Number}: {triage.Reason}");

					var written = adopter.Adopt(plan, onOutput);

					if (written.Count == 0)
					{
						idle++;
						onOutput(
							$"↺ #{triage.Issue.Number} left open: adoption wrote nothing, so closing it "
							+ "would close it against no change at all.");
						break;
					}

					onOutput($"✏️ Wrote {string.Join(", ", written)} in the local clone.");

					await CloseAsync(
							writeApi, owner, name, triage, AdoptedMarker, onOutput, cancellationToken)
						.ConfigureAwait(false);
					adopted++;

					if (triage.Proposal is { } adoptedProposal)
					{
						foreach (var bump in adoptedProposal.Bumps)
						{
							resolved[bump.Dependency] =
								$"{repositoryFullName} has adopted the proposed version locally";
						}
					}

					break;
```

Give `CloseAsync` and `CommentBody` a marker parameter, so the two close reasons share the machinery without sharing the marker:

```csharp
	private async Task CloseAsync(
		IGitHubWriteApi writeApi,
		string owner,
		string name,
		DependabotTriage triage,
		string marker,
		Action<string> onOutput,
		CancellationToken cancellationToken)
```

and inside it pass `CommentBody(triage, marker)`; in `CommentBody`, replace the hard-coded `ClosedMarker` with the parameter. Update the existing `AlreadySatisfied` call site to pass `ClosedMarker`.

Finally add `adopted` to the returned outcome, as its last argument.

`Restamp` needs no change: `Adoptable` is not `AlreadySatisfied`, so an adoptable pull request that was *not* closed stays in the list carrying its verdict — which is exactly the row the UI label in Task 9 is for.

- [ ] **Step 5: Run the tests**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj \
  -p:OutDir="$(pwd)/PanoramicData.NugetManagement.Test/bin/Verify/net10.0/"
./PanoramicData.NugetManagement.Test/bin/Verify/net10.0/PanoramicData.NugetManagement.Test.exe \
  --filter-class "*DependabotTriageRunnerTests"
```

Expected: PASS, including the three new tests and every existing one — the `AlreadySatisfied` path must be untouched.

- [ ] **Step 6: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Services/IBumpAdopter.cs \
        PanoramicData.NugetManagement.Web/Services/DependabotTriageRunner.cs \
        PanoramicData.NugetManagement.Test/DependabotTriageRunnerTests.cs
git commit -m "feat: adopt a bump then close its pull request, only if the write landed"
```

---

### Task 9: Wire it into the Fix lane and the tree

The last task: the triage work item gains a clone-backed adopter, and the tree gets a label for the new verdict.

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Services/WorkExecutors.cs:1276-1341` (`TriageDependabotAsync`)
- Modify: `PanoramicData.NugetManagement.Web/Components/RepositoryIssuesView.razor:90-99`
- Test: `PanoramicData.NugetManagement.Test/FixScopeTests.cs` (add one assertion)

**Interfaces:**
- Consumes: `IBumpAdopter` (Task 8), `DependabotAdoptionRemediation.Adopt` (Task 7).
- Produces: nothing new. `FixScope` is unchanged by design.

- [ ] **Step 1: Write the failing test**

Add to `FixScopeTests`:

```csharp
	[Fact]
	public void For_RepositoryDetail_StillTriagesWithoutANewAction()
	{
		var actions = FixScope.For(NavView.RepositoryDetail);

		actions.TriageDependabot.Should().BeTrue();
		typeof(FixActions).GetProperties().Should().HaveCount(
			2,
			"adoption arrives under the existing TriageDependabot action — Fix is the only control that "
				+ "fixes things, and a new action here would be a new button in all but name");
	}
```

- [ ] **Step 2: Run to verify it passes already**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj \
  -p:OutDir="$(pwd)/PanoramicData.NugetManagement.Test/bin/Verify/net10.0/"
./PanoramicData.NugetManagement.Test/bin/Verify/net10.0/PanoramicData.NugetManagement.Test.exe \
  --filter-class "*FixScopeTests"
```

Expected: PASS. This one is a guard rather than a driver — it fails later if somebody adds a `FixActions` member for adoption.

- [ ] **Step 3: Give the lane an adopter**

In `WorkExecutors.cs`, add this private class near the other nested helpers:

```csharp
	/// <summary>
	/// Adopts bumps into one repository's clone.
	/// </summary>
	/// <param name="localPath">The root of the clone.</param>
	private sealed class CloneBumpAdopter(string localPath) : IBumpAdopter
	{
		/// <inheritdoc />
		public IReadOnlyList<string> Adopt(DependabotAdoptionPlan plan, Action<string> onOutput)
			=> DependabotAdoptionRemediation.Adopt(localPath, plan, onOutput);
	}
```

In `TriageDependabotAsync`, after the context is built and before `Triage` is called, decide whether there is anywhere to write:

```csharp
		// Adoption writes files, which triage has never done. No clone means no adoption: the verdicts
		// are still reached and reported, and every adoptable pull request is left open saying why.
		var adopter = row is { IsClonedLocally: true, LocalPath: not null }
			? new CloneBumpAdopter(row.LocalPath)
			: null;

		if (adopter is null)
		{
			Say($"ℹ️ {row.RepositoryFullName} has no local clone, so nothing can be adopted this pass.");
		}
```

pass `adopter` as `RunAsync`'s last argument, and extend the summary line:

```csharp
		Say($"✅ {row.RepositoryFullName}: closed {outcome.Closed}, adopted {outcome.Adopted}, "
			+ $"{outcome.Covered} awaiting an existing fix, {outcome.Uncovered} with no fix available, "
			+ $"{outcome.Unrecognised} left alone.");
```

Add `using PanoramicData.NugetManagement.Web.Remediations;` if it is not already there.

**If the `ApplyRemediations` lane has a "clone is safe to write to" guard beyond `IsClonedLocally`** — check around `WorkExecutors.cs:829` — use the same guard here rather than inventing a second opinion about when a clone can be written.

- [ ] **Step 4: Label the verdict in the tree**

In `RepositoryIssuesView.razor`, the triage-label switch currently reads:

```csharp
		DependabotVerdict.ValidUncovered => "No auto-fix",
		_ => "Left alone"
```

Add the new case **above** them:

```csharp
		DependabotVerdict.Adoptable => "Adopting",
```

Present tense deliberately: an adopted pull request is closed and dropped from the open list, so the only rows that survive a pass carrying this verdict are ones where the write applied nothing and the close was withheld. "Adopted" on a row that is still open and still unbumped would be a lie.

**Do not put a Razor comment inside a tag while editing this file** — it compiles clean and throws at render time, and no test in the suite catches it.

- [ ] **Step 5: Run the full suite**

```bash
dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj \
  -p:OutDir="$(pwd)/PanoramicData.NugetManagement.Test/bin/Verify/net10.0/"
./PanoramicData.NugetManagement.Test/bin/Verify/net10.0/PanoramicData.NugetManagement.Test.exe
```

Run this with `run_in_background`: a full run takes ~19 minutes when the AI integration tests reach Ollama, past the 600s command cap.

Expected: every test passing except `DetachedProcessLaunchTests.Start_FromInsideAJobObject_LaunchesTheChildOutsideIt` (`FAIL_SKIP`), which is the known pre-existing failure. **Confirm that same test fails identically on the base commit before concluding anything about it.**

- [ ] **Step 6: See it work against the real repository**

Tests do not prove a Razor change renders, and nothing so far has exercised adoption end to end. Start the app, select `panoramicdata/Highlight.Api`, and press Fix.

Expected: the console announces adopting #6 and the four grouped pull requests, names the files written, and the summary line reports `adopted 5`. The working tree of the `Highlight.Api` clone then holds the version rewrites, uncommitted.

**Starting the app claims real work:** it restores the shared work queue and runs against real clones and the real GitHub. It will close those five pull requests for real. That is the intent, but do it deliberately and read the console before pressing anything else.

- [ ] **Step 7: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Services/WorkExecutors.cs \
        PanoramicData.NugetManagement.Web/Components/RepositoryIssuesView.razor \
        PanoramicData.NugetManagement.Test/FixScopeTests.cs
git commit -m "feat: adopt Dependabot bumps from the Fix lane"
```

---

## Self-Review

**Spec coverage:**

| Spec section | Task |
|---|---|
| Grouped PRs read from the body; format verified against real bodies | 1, 3 |
| `RepositoryIssue.Body`, `[JsonIgnore]`, mapped through | 2 |
| `DependabotProposal` → `Number` + `Bumps`; `DependabotBump` | 3 |
| Title fallback for a restored row with no body | 3 |
| Folding: satisfied drop out, least-resolved wins, covered before adoptable | 4, 6 |
| Reason names the bump that drove the verdict | 4 (`Describe`) |
| Gap issues raised per bump, not per PR | 4 (`GapBumps`) |
| `Adoptable` verdict, appended last | 6 |
| 60-day age gate, constant not a setting, `TimeProvider` | 6 |
| All-or-nothing adoptability | 6 (`Plan` returns null) |
| NuGet payload (`update_package_versions`) | 6, 7 |
| Actions payload (`replace_regex_in_files`), `PatternFor` extracted | 5, 6, 7 |
| Synthesized `RuleResult`; not registered in `RemediationRegistry` | 7 |
| Clone preconditions | 8 (no adopter → idle), 9 (lane decides) |
| Comment + close in the same pass, new marker | 8 |
| Close conditional on the write applying something | 8 |
| `DependabotTriageOutcome.Adopted`; summary line | 8, 9 |
| UI label; `FixScope` unchanged | 9 |

No spec requirement is unimplemented.

**Type consistency:** `DependabotBump`, `DependabotProposal.Bumps`, `DependabotAdoptionPlan.PackageUpdates`/`ActionPatterns`/`ActionReplacements`, `DependabotTriage.GapBumps`/`Adoption`, `ActionUsesPattern.Below`/`Replacement`/`WorkflowGlobs`, `IBumpAdopter.Adopt`, `DependabotAdoptionRemediation.Adopt`, `DependabotTriageRunner.AdoptedMarker`, `DependabotTriageOutcome.Adopted` and `DependabotTriageService.AdoptAfter` are each defined in one task and used with the same names and signatures in every later one.

**Two things the implementer must not smooth over:**

1. **Task 1 decides Tasks 3 and 6.** If the captured bodies do not match the assumed format, the regex changes to fit the fixtures. Never edit a fixture to fit the regex.
2. **Task 6's age gate will probably break existing triage tests**, whose pull requests are dated 2026-01-01 and are therefore months past the threshold under the real clock. The fix is a `FakeTimeProvider` in those tests, not a weaker gate.
