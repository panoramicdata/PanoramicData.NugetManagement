# Repository Issue Staleness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show every open GitHub issue and pull request under each repository in the sidebar, escalating anything a maintainer has not replied to for a week to Error and for a month to Critical.

**Architecture:** A narrow port (`IGitHubIssueApi`) hides Octokit behind three calls; `RepositoryIssueService` uses it to list open items and resolve each one's last maintainer comment by sweeping the repository's comments newest-first with a page budget and a per-item fallback. The result lands on `RepositoryDashboardRow.OpenIssues`, persisted by the existing dashboard cache, and feeds both a new `Issues (N)` tree node and the repository's existing failure totals.

**Tech Stack:** C# / .NET 10, Octokit 14, Blazor Server with PanoramicData.Blazor PDTree, xunit.v3 with AwesomeAssertions.

**Spec:** `docs/superpowers/specs/2026-08-30-repository-issue-staleness-design.md`

## Global Constraints

- **Tabs, not spaces.** Every file in this repository is tab-indented. Match it.
- **File-scoped namespaces.** `namespace Foo.Bar;` — never braces.
- **XML doc comments on every public type and member.** `GenerateDocumentationFile` is on and warnings are errors, so a missing `<summary>` fails the build.
- **Nullable is enabled and warnings are errors.** No `!` to silence a warning you can design away.
- **`.ConfigureAwait(false)` on every await in the core and Web service projects.** Follow the surrounding code.
- **Staleness thresholds are exactly 7 days (Error) and 30 days (Critical).** Named constants, not configuration.
- **Maintainer means `author_association` of `Owner`, `Member` or `Collaborator`.** Nothing else.
- **Bots get no exemption.** Never filter or downgrade by author login.
- **Run tests with the xunit v3 executable, never `dotnet test`.** `dotnet test` reports "Zero tests ran" in this repository. The command throughout this plan is:
  `cd PanoramicData.NugetManagement.Test/bin/Debug/net10.0 && ./PanoramicData.NugetManagement.Test.exe --filter-method '<pattern>'`
- **Stop any running dev server before building.** It locks the output assemblies and the build silently compiles nothing.
- **Baseline is 555 tests passing.** Every task must leave the whole suite green. The expected totals quoted in each task are arithmetic on that baseline (8 + 8 + 7 + 6 + 8 new tests across Tasks 1, 2, 3, 4 and 6, reaching 592); if another branch has landed tests since, what matters is that the count went up by the right amount and nothing went red.

---

### Task 1: The `RepositoryIssue` model and its severity bands

The pure heart of the feature: given when a maintainer last spoke, how bad is it? No I/O, no GitHub, no UI.

**Files:**
- Create: `PanoramicData.NugetManagement/Models/RepositoryIssue.cs`
- Test: `PanoramicData.NugetManagement.Test/RepositoryIssueSeverityTests.cs`

**Interfaces:**
- Consumes: `AssessmentSeverity` from `PanoramicData.NugetManagement.Models` (values `Info`, `Warning`, `Error`, `Critical`).
- Produces: `RepositoryIssue` with settable properties `Number` (int), `Title` (string), `IsPullRequest` (bool), `HtmlUrl` (string), `AuthorLogin` (string), `CreatedAtUtc` (DateTimeOffset), `LastMaintainerReplyUtc` (DateTimeOffset?); computed `ClockStartUtc` (DateTimeOffset); method `SeverityAt(DateTimeOffset nowUtc)` returning `AssessmentSeverity`; constants `StaleAfter` and `CriticalAfter` (TimeSpan).

- [ ] **Step 1: Write the failing tests**

Create `PanoramicData.NugetManagement.Test/RepositoryIssueSeverityTests.cs`:

```csharp
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests the staleness bands of <see cref="RepositoryIssue"/>.
/// </summary>
public class RepositoryIssueSeverityTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

	private static RepositoryIssue Replied(TimeSpan ago)
		=> new()
		{
			Number = 1,
			Title = "Something",
			HtmlUrl = "https://github.com/panoramicdata/Sample/issues/1",
			AuthorLogin = "reporter",
			CreatedAtUtc = Now - TimeSpan.FromDays(365),
			LastMaintainerReplyUtc = Now - ago
		};

	[Fact]
	public void AReplyMinutesAgoIsInformational()
		=> Replied(TimeSpan.FromMinutes(5)).SeverityAt(Now).Should().Be(AssessmentSeverity.Info);

	[Fact]
	public void AMomentUnderSevenDaysIsStillInformational()
		=> Replied(TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1)).SeverityAt(Now)
			.Should().Be(AssessmentSeverity.Info);

	[Fact]
	public void ExactlySevenDaysIsAnError()
		=> Replied(TimeSpan.FromDays(7)).SeverityAt(Now).Should().Be(AssessmentSeverity.Error);

	[Fact]
	public void AMomentUnderThirtyDaysIsStillAnError()
		=> Replied(TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1)).SeverityAt(Now)
			.Should().Be(AssessmentSeverity.Error);

	[Fact]
	public void ExactlyThirtyDaysIsCritical()
		=> Replied(TimeSpan.FromDays(30)).SeverityAt(Now).Should().Be(AssessmentSeverity.Critical);

	[Fact]
	public void NoMaintainerReplyEverBandsOnTheCreationDate()
	{
		var issue = new RepositoryIssue
		{
			Number = 2,
			Title = "Never answered",
			HtmlUrl = "https://github.com/panoramicdata/Sample/issues/2",
			AuthorLogin = "reporter",
			CreatedAtUtc = Now - TimeSpan.FromDays(31),
			LastMaintainerReplyUtc = null
		};

		issue.ClockStartUtc.Should().Be(issue.CreatedAtUtc);
		issue.SeverityAt(Now).Should().Be(AssessmentSeverity.Critical);
	}

	[Fact]
	public void ABotAuthoredItemBandsExactlyAsAHumanOneDoes()
	{
		var bot = new RepositoryIssue
		{
			Number = 3,
			Title = "Bump Newtonsoft.Json from 13.0.3 to 13.0.4",
			IsPullRequest = true,
			HtmlUrl = "https://github.com/panoramicdata/Sample/pull/3",
			AuthorLogin = "dependabot[bot]",
			CreatedAtUtc = Now - TimeSpan.FromDays(40),
			LastMaintainerReplyUtc = null
		};

		bot.SeverityAt(Now).Should().Be(AssessmentSeverity.Critical);
	}

	[Fact]
	public void TheBandsNeverReturnWarning()
	{
		var days = Enumerable.Range(0, 120).Select(d => Replied(TimeSpan.FromDays(d)));
		days.Should().AllSatisfy(i => i.SeverityAt(Now).Should().NotBe(AssessmentSeverity.Warning));
	}
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet build PanoramicData.NugetManagement.slnx -c Debug
```

Expected: FAIL to compile, with `CS0246: The type or namespace name 'RepositoryIssue' could not be found`.

- [ ] **Step 3: Write the model**

Create `PanoramicData.NugetManagement/Models/RepositoryIssue.cs`:

```csharp
namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// One open GitHub issue or pull request, and how long it has gone without a maintainer reply.
/// </summary>
/// <remarks>
/// Issues and pull requests share this type because GitHub's own model does: a pull request is an
/// issue, the list endpoint returns both, and "how long since one of us answered" is the same
/// question for each. <see cref="IsPullRequest"/> separates them where the UI needs to and nowhere
/// else.
/// </remarks>
public class RepositoryIssue
{
	/// <summary>
	/// How long without a maintainer reply before an item is an error.
	/// </summary>
	public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

	/// <summary>
	/// How long without a maintainer reply before an item is critical.
	/// </summary>
	public static readonly TimeSpan CriticalAfter = TimeSpan.FromDays(30);

	/// <summary>The issue or pull request number.</summary>
	public required int Number { get; init; }

	/// <summary>The title, as shown on GitHub.</summary>
	public required string Title { get; init; }

	/// <summary>Whether this item is a pull request rather than an issue.</summary>
	public bool IsPullRequest { get; init; }

	/// <summary>The GitHub web address of the item.</summary>
	public required string HtmlUrl { get; init; }

	/// <summary>The login of whoever opened it. Bots included, and never filtered on.</summary>
	public required string AuthorLogin { get; init; }

	/// <summary>When the item was opened.</summary>
	public required DateTimeOffset CreatedAtUtc { get; init; }

	/// <summary>
	/// When a maintainer — someone whose author association on the comment was Owner, Member or
	/// Collaborator — last commented, or null if none ever has.
	/// </summary>
	public DateTimeOffset? LastMaintainerReplyUtc { get; init; }

	/// <summary>
	/// The instant the staleness clock starts: the last maintainer reply, or the moment the item was
	/// opened when there has never been one. An item nobody has answered has been waiting since it
	/// was raised.
	/// </summary>
	public DateTimeOffset ClockStartUtc => LastMaintainerReplyUtc ?? CreatedAtUtc;

	/// <summary>
	/// How bad this item is at the given instant.
	/// </summary>
	/// <param name="nowUtc">The instant to judge against.</param>
	/// <remarks>
	/// Derived rather than stored, and takes the instant as a parameter rather than reading the
	/// clock. A cached item then reports today's severity when it is read back tomorrow, instead of
	/// a verdict frozen when the network last answered — and the bands are testable without a clock
	/// abstraction. There is deliberately no Warning band: two escalations were asked for, and a
	/// third step in the middle would mean nothing.
	/// </remarks>
	public AssessmentSeverity SeverityAt(DateTimeOffset nowUtc)
	{
		var age = nowUtc - ClockStartUtc;

		if (age >= CriticalAfter)
		{
			return AssessmentSeverity.Critical;
		}

		return age >= StaleAfter
			? AssessmentSeverity.Error
			: AssessmentSeverity.Info;
	}
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet build PanoramicData.NugetManagement.slnx -c Debug
cd PanoramicData.NugetManagement.Test/bin/Debug/net10.0 && ./PanoramicData.NugetManagement.Test.exe --filter-method '*RepositoryIssueSeverityTests*'
```

Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add PanoramicData.NugetManagement/Models/RepositoryIssue.cs PanoramicData.NugetManagement.Test/RepositoryIssueSeverityTests.cs
git commit -m "Add RepositoryIssue and its staleness bands"
```

---

### Task 2: The port and the comment sweep

The logic that turns "open items" plus "comments" into "when did a maintainer last speak", including the page budget and the fallback. Tested entirely against a hand-written fake, because the project has no mocking library and `IGitHubClient` is far too wide to implement by hand.

**Files:**
- Create: `PanoramicData.NugetManagement/Services/IGitHubIssueApi.cs`
- Create: `PanoramicData.NugetManagement/Services/RepositoryIssueService.cs`
- Test: `PanoramicData.NugetManagement.Test/RepositoryIssueServiceTests.cs`

**Interfaces:**
- Consumes: `RepositoryIssue` from Task 1.
- Produces:
  - `GitHubOpenItem` — record with `int Number`, `string Title`, `bool IsPullRequest`, `string HtmlUrl`, `string AuthorLogin`, `DateTimeOffset CreatedAtUtc`.
  - `GitHubIssueComment` — record with `int IssueNumber`, `DateTimeOffset CreatedAtUtc`, `bool IsFromMaintainer`.
  - `IGitHubIssueApi` — `Task<IReadOnlyList<GitHubOpenItem>> GetOpenItemsAsync(string owner, string name, CancellationToken cancellationToken)`, `Task<IReadOnlyList<GitHubIssueComment>> GetRepositoryCommentsPageAsync(string owner, string name, int pageNumber, CancellationToken cancellationToken)`, `Task<IReadOnlyList<GitHubIssueComment>> GetCommentsForItemAsync(string owner, string name, int issueNumber, CancellationToken cancellationToken)`.
  - `RepositoryIssueService` — constructor takes `IGitHubIssueApi`; `Task<IReadOnlyList<RepositoryIssue>> GetOpenIssuesAsync(string owner, string name, CancellationToken cancellationToken)`; `public const int MaxSweepPages = 5`.

- [ ] **Step 1: Write the failing tests**

Create `PanoramicData.NugetManagement.Test/RepositoryIssueServiceTests.cs`:

```csharp
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests the comment sweep of <see cref="RepositoryIssueService"/> against a fake GitHub API.
/// </summary>
public class RepositoryIssueServiceTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

	private static GitHubOpenItem Item(int number, bool isPullRequest = false)
		=> new(
			number,
			$"Item {number}",
			isPullRequest,
			$"https://github.com/panoramicdata/Sample/issues/{number}",
			"reporter",
			Now - TimeSpan.FromDays(200));

	/// <summary>
	/// A fake API returning fixed open items and fixed pages of comments, counting what was asked
	/// for so the tests can assert on the cost of the sweep.
	/// </summary>
	private sealed class FakeApi(
		IReadOnlyList<GitHubOpenItem> items,
		IReadOnlyList<IReadOnlyList<GitHubIssueComment>> pages,
		IReadOnlyDictionary<int, IReadOnlyList<GitHubIssueComment>>? perItem = null)
		: IGitHubIssueApi
	{
		public int PagesRequested { get; private set; }

		public List<int> ItemsFetchedIndividually { get; } = [];

		public Task<IReadOnlyList<GitHubOpenItem>> GetOpenItemsAsync(
			string owner, string name, CancellationToken cancellationToken)
			=> Task.FromResult(items);

		public Task<IReadOnlyList<GitHubIssueComment>> GetRepositoryCommentsPageAsync(
			string owner, string name, int pageNumber, CancellationToken cancellationToken)
		{
			PagesRequested = Math.Max(PagesRequested, pageNumber);
			return Task.FromResult(pageNumber <= pages.Count ? pages[pageNumber - 1] : []);
		}

		public Task<IReadOnlyList<GitHubIssueComment>> GetCommentsForItemAsync(
			string owner, string name, int issueNumber, CancellationToken cancellationToken)
		{
			ItemsFetchedIndividually.Add(issueNumber);
			return Task.FromResult(
				perItem is not null && perItem.TryGetValue(issueNumber, out var found) ? found : []);
		}
	}

	private static GitHubIssueComment Comment(int issueNumber, TimeSpan ago, bool maintainer)
		=> new(issueNumber, Now - ago, maintainer);

	[Fact]
	public async Task OpenIssuesAndPullRequestsBothAppearWithTheirKind()
	{
		var api = new FakeApi([Item(1), Item(2, isPullRequest: true)], [[]]);
		var service = new RepositoryIssueService(api);

		var result = await service.GetOpenIssuesAsync(
			"panoramicdata", "Sample", TestContext.Current.CancellationToken);

		result.Should().HaveCount(2);
		result.Single(i => i.Number == 1).IsPullRequest.Should().BeFalse();
		result.Single(i => i.Number == 2).IsPullRequest.Should().BeTrue();
	}

	[Fact]
	public async Task TheNewestMaintainerCommentIsTheLastReply()
	{
		var api = new FakeApi(
			[Item(1)],
			[[
				Comment(1, TimeSpan.FromDays(2), maintainer: true),
				Comment(1, TimeSpan.FromDays(9), maintainer: true)
			]]);

		var result = await new RepositoryIssueService(api).GetOpenIssuesAsync(
			"panoramicdata", "Sample", TestContext.Current.CancellationToken);

		result.Single().LastMaintainerReplyUtc.Should().Be(Now - TimeSpan.FromDays(2));
	}

	[Fact]
	public async Task ACommentFromTheReporterDoesNotCountAsAReply()
	{
		var api = new FakeApi(
			[Item(1)],
			[[
				Comment(1, TimeSpan.FromDays(1), maintainer: false),
				Comment(1, TimeSpan.FromDays(60), maintainer: true)
			]]);

		var result = await new RepositoryIssueService(api).GetOpenIssuesAsync(
			"panoramicdata", "Sample", TestContext.Current.CancellationToken);

		result.Single().LastMaintainerReplyUtc.Should().Be(Now - TimeSpan.FromDays(60));
	}

	[Fact]
	public async Task TheSweepStopsAsSoonAsEveryItemIsResolved()
	{
		var api = new FakeApi(
			[Item(1)],
			[
				[Comment(1, TimeSpan.FromDays(1), maintainer: true)],
				[Comment(1, TimeSpan.FromDays(50), maintainer: true)]
			]);

		await new RepositoryIssueService(api).GetOpenIssuesAsync(
			"panoramicdata", "Sample", TestContext.Current.CancellationToken);

		api.PagesRequested.Should().Be(1, "every open item was resolved by the first page");
	}

	[Fact]
	public async Task TheSweepStopsAtThePageBudgetAndFallsBackPerItem()
	{
		var emptyPages = Enumerable.Range(0, 20)
			.Select(_ => (IReadOnlyList<GitHubIssueComment>)[])
			.ToList();

		var api = new FakeApi(
			[Item(1)],
			emptyPages,
			new Dictionary<int, IReadOnlyList<GitHubIssueComment>>
			{
				[1] = [Comment(1, TimeSpan.FromDays(3), maintainer: true)]
			});

		var result = await new RepositoryIssueService(api).GetOpenIssuesAsync(
			"panoramicdata", "Sample", TestContext.Current.CancellationToken);

		api.PagesRequested.Should().Be(RepositoryIssueService.MaxSweepPages);
		api.ItemsFetchedIndividually.Should().Equal(1);
		result.Single().LastMaintainerReplyUtc.Should().Be(Now - TimeSpan.FromDays(3));
	}

	[Fact]
	public async Task AnItemNoMaintainerEverAnsweredHasNoReplyTime()
	{
		var api = new FakeApi([Item(1)], [[Comment(1, TimeSpan.FromDays(2), maintainer: false)]]);

		var result = await new RepositoryIssueService(api).GetOpenIssuesAsync(
			"panoramicdata", "Sample", TestContext.Current.CancellationToken);

		result.Single().LastMaintainerReplyUtc.Should().BeNull();
	}

	[Fact]
	public async Task AnExhaustedSweepStopsWithoutHittingTheBudget()
	{
		var api = new FakeApi([Item(1)], [[]]);

		await new RepositoryIssueService(api).GetOpenIssuesAsync(
			"panoramicdata", "Sample", TestContext.Current.CancellationToken);

		api.PagesRequested.Should().Be(1, "a short page means the comments ran out");
	}

	[Fact]
	public async Task CommentsOnOtherItemsAreIgnored()
	{
		var api = new FakeApi(
			[Item(1)],
			[[Comment(99, TimeSpan.FromDays(1), maintainer: true)]]);

		var result = await new RepositoryIssueService(api).GetOpenIssuesAsync(
			"panoramicdata", "Sample", TestContext.Current.CancellationToken);

		result.Single().LastMaintainerReplyUtc.Should().BeNull();
	}
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet build PanoramicData.NugetManagement.slnx -c Debug
```

Expected: FAIL to compile, with `CS0246` for `IGitHubIssueApi`, `GitHubOpenItem`, `GitHubIssueComment` and `RepositoryIssueService`.

- [ ] **Step 3: Write the port**

Create `PanoramicData.NugetManagement/Services/IGitHubIssueApi.cs`:

```csharp
namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// One open issue or pull request, as the issue API reports it.
/// </summary>
/// <param name="Number">The issue or pull request number.</param>
/// <param name="Title">The title.</param>
/// <param name="IsPullRequest">Whether the item is a pull request rather than an issue.</param>
/// <param name="HtmlUrl">The GitHub web address of the item.</param>
/// <param name="AuthorLogin">The login of whoever opened it.</param>
/// <param name="CreatedAtUtc">When it was opened.</param>
public record GitHubOpenItem(
	int Number,
	string Title,
	bool IsPullRequest,
	string HtmlUrl,
	string AuthorLogin,
	DateTimeOffset CreatedAtUtc);

/// <summary>
/// One comment, reduced to the three facts the staleness measure needs.
/// </summary>
/// <param name="IssueNumber">The issue or pull request the comment is on.</param>
/// <param name="CreatedAtUtc">When the comment was written.</param>
/// <param name="IsFromMaintainer">
/// Whether its author association was Owner, Member or Collaborator. Deciding this at the adapter
/// keeps GitHub's association vocabulary out of the sweep.
/// </param>
public record GitHubIssueComment(
	int IssueNumber,
	DateTimeOffset CreatedAtUtc,
	bool IsFromMaintainer);

/// <summary>
/// The narrow slice of the GitHub issue API this feature needs.
/// </summary>
/// <remarks>
/// A port rather than a direct dependency on <c>IGitHubClient</c>, which has hundreds of members and
/// cannot be implemented by hand — and this project has no mocking library. The same seam
/// <c>ICodacyIssueService</c> uses.
/// </remarks>
public interface IGitHubIssueApi
{
	/// <summary>
	/// Every open issue and pull request in a repository.
	/// </summary>
	/// <param name="owner">The repository owner.</param>
	/// <param name="name">The repository name.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	Task<IReadOnlyList<GitHubOpenItem>> GetOpenItemsAsync(
		string owner,
		string name,
		CancellationToken cancellationToken);

	/// <summary>
	/// One page of the repository's issue comments, newest first, 100 to a page.
	/// </summary>
	/// <param name="owner">The repository owner.</param>
	/// <param name="name">The repository name.</param>
	/// <param name="pageNumber">The one-based page number.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>The page, or an empty list once the comments run out.</returns>
	Task<IReadOnlyList<GitHubIssueComment>> GetRepositoryCommentsPageAsync(
		string owner,
		string name,
		int pageNumber,
		CancellationToken cancellationToken);

	/// <summary>
	/// Every comment on one issue or pull request.
	/// </summary>
	/// <param name="owner">The repository owner.</param>
	/// <param name="name">The repository name.</param>
	/// <param name="issueNumber">The issue or pull request number.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	Task<IReadOnlyList<GitHubIssueComment>> GetCommentsForItemAsync(
		string owner,
		string name,
		int issueNumber,
		CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Write the sweep**

Create `PanoramicData.NugetManagement/Services/RepositoryIssueService.cs`:

```csharp
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Builds the list of open issues and pull requests for a repository, each carrying the time a
/// maintainer last commented on it.
/// </summary>
/// <remarks>
/// The naive implementation fetches every item's comments, costing one request per open item. A
/// repository with a Dependabot backlog would spend a large share of a 5,000/hour budget on a single
/// refresh. So the repository's comments are swept newest-first instead: walked in that order, the
/// first maintainer comment seen for an item is that item's last maintainer reply, and the walk
/// stops as soon as every open item is answered. Repositories whose recent conversation is mostly on
/// currently-open items — the normal case — cost one or two pages.
/// </remarks>
public class RepositoryIssueService(IGitHubIssueApi api)
{
	/// <summary>
	/// How many pages of repository comments the sweep will read before giving up and asking about
	/// the remaining items one at a time. Bounds the cost of a repository with thousands of comments
	/// on long-closed issues, without making any single answer less exact.
	/// </summary>
	public const int MaxSweepPages = 5;

	private readonly IGitHubIssueApi _api = api;

	/// <summary>
	/// The open issues and pull requests of a repository.
	/// </summary>
	/// <param name="owner">The repository owner.</param>
	/// <param name="name">The repository name.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	public async Task<IReadOnlyList<RepositoryIssue>> GetOpenIssuesAsync(
		string owner,
		string name,
		CancellationToken cancellationToken)
	{
		var items = await _api
			.GetOpenItemsAsync(owner, name, cancellationToken)
			.ConfigureAwait(false);

		if (items.Count == 0)
		{
			return [];
		}

		var replies = new Dictionary<int, DateTimeOffset>();
		var unresolved = items.Select(item => item.Number).ToHashSet();

		for (var page = 1; page <= MaxSweepPages && unresolved.Count > 0; page++)
		{
			var comments = await _api
				.GetRepositoryCommentsPageAsync(owner, name, page, cancellationToken)
				.ConfigureAwait(false);

			foreach (var comment in comments)
			{
				if (!comment.IsFromMaintainer || !unresolved.Contains(comment.IssueNumber))
				{
					continue;
				}

				// Newest-first, so the first maintainer comment seen for an item is its latest.
				replies[comment.IssueNumber] = comment.CreatedAtUtc;
				unresolved.Remove(comment.IssueNumber);
			}

			// A short page means the comments ran out; there is nothing further to ask for.
			if (comments.Count == 0)
			{
				unresolved.Clear();
				break;
			}
		}

		// Anything the budget could not reach is asked about directly, so that an item whose last
		// maintainer comment lies beyond the swept pages still gets an exact answer rather than
		// being reported as never answered.
		foreach (var number in items.Select(i => i.Number).Where(n => !replies.ContainsKey(n)))
		{
			var comments = await _api
				.GetCommentsForItemAsync(owner, name, number, cancellationToken)
				.ConfigureAwait(false);

			var latest = comments
				.Where(c => c.IsFromMaintainer)
				.Select(c => (DateTimeOffset?)c.CreatedAtUtc)
				.DefaultIfEmpty(null)
				.Max();

			if (latest is not null)
			{
				replies[number] = latest.Value;
			}
		}

		return [.. items.Select(item => new RepositoryIssue
		{
			Number = item.Number,
			Title = item.Title,
			IsPullRequest = item.IsPullRequest,
			HtmlUrl = item.HtmlUrl,
			AuthorLogin = item.AuthorLogin,
			CreatedAtUtc = item.CreatedAtUtc,
			LastMaintainerReplyUtc = replies.TryGetValue(item.Number, out var reply) ? reply : null
		})];
	}
}
```

Note on the exhausted-sweep path: clearing `unresolved` skips straight to the fallback loop, which asks about each still-unanswered item once. In `AnExhaustedSweepStopsWithoutHittingTheBudget` that costs one per-item call and returns null, which is correct — no maintainer ever commented.

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet build PanoramicData.NugetManagement.slnx -c Debug
cd PanoramicData.NugetManagement.Test/bin/Debug/net10.0 && ./PanoramicData.NugetManagement.Test.exe --filter-method '*RepositoryIssueServiceTests*'
```

Expected: PASS, 8 tests.

- [ ] **Step 6: Commit**

```bash
git add PanoramicData.NugetManagement/Services/IGitHubIssueApi.cs PanoramicData.NugetManagement/Services/RepositoryIssueService.cs PanoramicData.NugetManagement.Test/RepositoryIssueServiceTests.cs
git commit -m "Sweep repository comments for the last maintainer reply"
```

---

### Task 3: The Octokit adapter

The one Octokit-facing implementation of the port. Deliberately thin: translation only, so there is nothing in it worth a unit test.

**Files:**
- Create: `PanoramicData.NugetManagement/Services/OctokitGitHubIssueApi.cs`
- Test: `PanoramicData.NugetManagement.Test/MaintainerAssociationTests.cs`

**Interfaces:**
- Consumes: `IGitHubIssueApi`, `GitHubOpenItem`, `GitHubIssueComment` from Task 2; `Octokit.IGitHubClient`.
- Produces: `OctokitGitHubIssueApi` with constructor `OctokitGitHubIssueApi(IGitHubClient github)` and `public static bool IsMaintainerAssociation(AuthorAssociation association)`.

- [ ] **Step 1: Write the failing test for the association vocabulary**

Which associations mean "maintainer" is the one real decision in this class, so it is exposed as a static and tested. Create `PanoramicData.NugetManagement.Test/MaintainerAssociationTests.cs`:

```csharp
using Octokit;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests which GitHub author associations count as a maintainer of the repository.
/// </summary>
public class MaintainerAssociationTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Theory]
	[InlineData(AuthorAssociation.Owner)]
	[InlineData(AuthorAssociation.Member)]
	[InlineData(AuthorAssociation.Collaborator)]
	public void WriteAccessMeansMaintainer(AuthorAssociation association)
		=> OctokitGitHubIssueApi.IsMaintainerAssociation(association).Should().BeTrue();

	[Theory]
	[InlineData(AuthorAssociation.Contributor)]
	[InlineData(AuthorAssociation.FirstTimeContributor)]
	[InlineData(AuthorAssociation.FirstTimer)]
	[InlineData(AuthorAssociation.None)]
	public void EveryoneElseIsSomeoneWaitingForAnAnswer(AuthorAssociation association)
		=> OctokitGitHubIssueApi.IsMaintainerAssociation(association).Should().BeFalse();
}
```

If Octokit 14 names any of these members differently, correct the test to the real enum values — the rule is Owner, Member and Collaborator in, everything else out.

- [ ] **Step 2: Write the adapter**

Create `PanoramicData.NugetManagement/Services/OctokitGitHubIssueApi.cs`:

```csharp
using Octokit;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// The Octokit-backed <see cref="IGitHubIssueApi"/>.
/// </summary>
/// <remarks>
/// Translation only. Every decision about what the data means — which associations count as a
/// maintainer, how far to sweep, what to do with what is left — belongs to
/// <see cref="RepositoryIssueService"/>, which can be tested. This class is kept thin enough that
/// there is nothing here to get wrong beyond the field names.
/// </remarks>
public class OctokitGitHubIssueApi(IGitHubClient github) : IGitHubIssueApi
{
	private const int PageSize = 100;

	private readonly IGitHubClient _github = github;

	/// <inheritdoc />
	public async Task<IReadOnlyList<GitHubOpenItem>> GetOpenItemsAsync(
		string owner,
		string name,
		CancellationToken cancellationToken)
	{
		// State.Open is the default, but saying it means the intent survives a future edit. The
		// endpoint returns pull requests alongside issues, which is what this feature wants.
		var request = new RepositoryIssueRequest { State = ItemStateFilter.Open };

		var issues = await _github.Issue
			.GetAllForRepository(owner, name, request, new ApiOptions { PageSize = PageSize })
			.ConfigureAwait(false);

		return [.. issues.Select(issue => new GitHubOpenItem(
			issue.Number,
			issue.Title ?? string.Empty,
			issue.PullRequest is not null,
			issue.HtmlUrl ?? string.Empty,
			issue.User?.Login ?? string.Empty,
			issue.CreatedAt))];
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<GitHubIssueComment>> GetRepositoryCommentsPageAsync(
		string owner,
		string name,
		int pageNumber,
		CancellationToken cancellationToken)
	{
		var request = new IssueCommentRequest
		{
			Sort = IssueCommentSort.Created,
			Direction = SortDirection.Descending
		};

		var options = new ApiOptions
		{
			PageSize = PageSize,
			PageCount = 1,
			StartPage = pageNumber
		};

		var comments = await _github.Issue.Comment
			.GetAllForRepository(owner, name, request, options)
			.ConfigureAwait(false);

		return [.. comments.Select(Translate)];
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<GitHubIssueComment>> GetCommentsForItemAsync(
		string owner,
		string name,
		int issueNumber,
		CancellationToken cancellationToken)
	{
		var comments = await _github.Issue.Comment
			.GetAllForIssue(owner, name, issueNumber, new ApiOptions { PageSize = PageSize })
			.ConfigureAwait(false);

		return [.. comments.Select(comment => Translate(comment, issueNumber))];
	}

	/// <summary>
	/// Whether an author association means the commenter has write access to the repository, and so
	/// that their comment counts as us having answered.
	/// </summary>
	/// <param name="association">The association GitHub reported on the comment.</param>
	/// <remarks>
	/// Public and static because it is the one judgement this otherwise mechanical class makes, and
	/// the only part of it worth a test.
	/// </remarks>
	public static bool IsMaintainerAssociation(AuthorAssociation association)
		=> association is AuthorAssociation.Owner
			or AuthorAssociation.Member
			or AuthorAssociation.Collaborator;

	/// <summary>
	/// Whether a comment's author association makes its writer a maintainer of the repository.
	/// </summary>
	private static bool IsMaintainer(IssueComment comment)
		=> IsMaintainerAssociation(comment.AuthorAssociation.Value);

	/// <summary>
	/// Translates a comment, taking its issue number from the URL GitHub returns on it.
	/// </summary>
	private static GitHubIssueComment Translate(IssueComment comment)
		=> new(IssueNumberFrom(comment.HtmlUrl), comment.CreatedAt, IsMaintainer(comment));

	/// <summary>
	/// Translates a comment whose issue number the caller already knows.
	/// </summary>
	private static GitHubIssueComment Translate(IssueComment comment, int issueNumber)
		=> new(issueNumber, comment.CreatedAt, IsMaintainer(comment));

	/// <summary>
	/// The issue number in a comment's web address, which ends "/issues/123#issuecomment-456" or
	/// "/pull/123#issuecomment-456". The repository-wide comment endpoint identifies the issue only
	/// by URL, so this is the only place the number can come from.
	/// </summary>
	private static int IssueNumberFrom(string? htmlUrl)
	{
		if (string.IsNullOrEmpty(htmlUrl))
		{
			return 0;
		}

		var withoutFragment = htmlUrl.Split('#')[0];
		var lastSegment = withoutFragment[(withoutFragment.LastIndexOf('/') + 1)..];

		return int.TryParse(lastSegment, out var number) ? number : 0;
	}
}
```

- [ ] **Step 3: Build to verify the Octokit member names are right**

```bash
dotnet build PanoramicData.NugetManagement.slnx -c Debug
```

Expected: PASS.

If the build fails on an Octokit member, the shape is what to fix, not the design. The likely points of difference in Octokit 14 are: `IssueComment.AuthorAssociation` may be a plain `AuthorAssociation` rather than a `StringEnum<AuthorAssociation>` (drop the `.Value`); `IssueCommentRequest` may not expose `Sort`/`Direction` (in which case sweep the default order and rely on the per-item fallback, and say so in a comment); and `ApiOptions.StartPage`/`PageCount` are the paging levers. Adjust the adapter only — `IGitHubIssueApi` and `RepositoryIssueService` must not change.

- [ ] **Step 4: Run the full suite**

```bash
cd PanoramicData.NugetManagement.Test/bin/Debug/net10.0 && ./PanoramicData.NugetManagement.Test.exe
```

Expected: PASS, 578 tests (555 baseline + 16 from Tasks 1 and 2 + 7 here).

- [ ] **Step 5: Commit**

```bash
git add PanoramicData.NugetManagement/Services/OctokitGitHubIssueApi.cs PanoramicData.NugetManagement.Test/MaintainerAssociationTests.cs
git commit -m "Back the issue port with Octokit"
```

---

### Task 4: Rolling stale items into repository health

Puts the data on the row, makes it count, and stops an ungoverned repository carrying issue findings it should no longer have.

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Models/RepositoryDashboardRow.cs:151-181` (add `OpenIssues`, change the four totals)
- Modify: `PanoramicData.NugetManagement.Web/Services/DashboardCacheService.cs:47` (`DiscoveryVersion` 3 → 4)
- Modify: `PanoramicData.NugetManagement.Web/Services/GovernanceScope.cs:71` (clear `OpenIssues` alongside `Assessment`)
- Test: `PanoramicData.NugetManagement.Test/RepositoryIssueRollupTests.cs`

**Interfaces:**
- Consumes: `RepositoryIssue` from Task 1.
- Produces: `RepositoryDashboardRow.OpenIssues` (`List<RepositoryIssue>`, defaults to empty), `RepositoryDashboardRow.StaleIssues` (`IEnumerable<RepositoryIssue>`), and totals that include them.

- [ ] **Step 1: Write the failing tests**

Create `PanoramicData.NugetManagement.Test/RepositoryIssueRollupTests.cs`:

```csharp
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that stale issues count as repository failures, and that fresh ones do not.
/// </summary>
public class RepositoryIssueRollupTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RepositoryIssue Aged(int number, int daysSinceReply)
		=> new()
		{
			Number = number,
			Title = $"Item {number}",
			HtmlUrl = $"https://github.com/panoramicdata/Sample/issues/{number}",
			AuthorLogin = "reporter",
			CreatedAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromDays(400),
			LastMaintainerReplyUtc = DateTimeOffset.UtcNow - TimeSpan.FromDays(daysSinceReply)
		};

	private static RepositoryDashboardRow Row(RepoAssessment? assessment, params RepositoryIssue[] issues)
		=> new()
		{
			RepositoryFullName = "panoramicdata/Sample",
			Assessment = assessment,
			OpenIssues = [.. issues]
		};

	private static RepoAssessment CleanAssessment()
		=> new()
		{
			RepositoryFullName = "panoramicdata/Sample",
			DefaultBranch = "main",
			AssessedAtUtc = DateTimeOffset.UtcNow,
			RuleResults = []
		};

	[Fact]
	public void AFreshIssueIsNotAFailure()
	{
		var row = Row(CleanAssessment(), Aged(1, daysSinceReply: 1));

		row.TotalFailures.Should().Be(0);
		row.TotalErrors.Should().Be(0);
		row.TotalCriticals.Should().Be(0);
		row.HealthStatus.Should().Be(PackageHealthStatus.Success);
	}

	[Fact]
	public void AWeekOldIssueIsAnErrorFailure()
	{
		var row = Row(CleanAssessment(), Aged(1, daysSinceReply: 8));

		row.TotalFailures.Should().Be(1);
		row.TotalErrors.Should().Be(1);
		row.TotalCriticals.Should().Be(0);
		row.HealthStatus.Should().Be(PackageHealthStatus.Error);
	}

	[Fact]
	public void AMonthOldIssueIsACriticalFailure()
	{
		var row = Row(CleanAssessment(), Aged(1, daysSinceReply: 45));

		row.TotalFailures.Should().Be(1);
		row.TotalCriticals.Should().Be(1);
		row.TotalErrors.Should().Be(0);
		row.HealthStatus.Should().Be(PackageHealthStatus.Error);
	}

	[Fact]
	public void IssueFailuresAddToRuleFailuresRatherThanReplacingThem()
	{
		var assessment = new RepoAssessment
		{
			RepositoryFullName = "panoramicdata/Sample",
			DefaultBranch = "main",
			AssessedAtUtc = DateTimeOffset.UtcNow,
			RuleResults =
			[
				new RuleResult
				{
					RuleId = "PKG-01",
					RuleName = "Package id set",
					Category = AssessmentCategory.ProjectMetadata,
					Severity = AssessmentSeverity.Error,
					Passed = false,
					Message = "missing"
				}
			]
		};

		var row = Row(assessment, Aged(1, daysSinceReply: 45), Aged(2, daysSinceReply: 2));

		row.TotalFailures.Should().Be(2, "one failing rule and one critical issue; the fresh issue is neither");
		row.TotalErrors.Should().Be(1);
		row.TotalCriticals.Should().Be(1);
	}

	[Fact]
	public void AnUnassessedRepositoryStaysUnknownHoweverStaleItsIssues()
	{
		var row = Row(assessment: null, Aged(1, daysSinceReply: 90));

		row.HealthStatus.Should().Be(PackageHealthStatus.Unknown,
			"not assessed is not the same as assessed and bad");
	}

	[Fact]
	public void ARowWithNoIssuesBehavesExactlyAsBefore()
	{
		var row = Row(CleanAssessment());

		row.OpenIssues.Should().BeEmpty();
		row.TotalFailures.Should().Be(0);
		row.HealthStatus.Should().Be(PackageHealthStatus.Success);
	}
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet build PanoramicData.NugetManagement.slnx -c Debug
```

Expected: FAIL to compile, with `CS0117: 'RepositoryDashboardRow' does not contain a definition for 'OpenIssues'`.

- [ ] **Step 3: Add `OpenIssues` and fold it into the totals**

In `PanoramicData.NugetManagement.Web/Models/RepositoryDashboardRow.cs`, add after the `CategorySummaries` property:

```csharp
	/// <summary>
	/// The open GitHub issues and pull requests of this repository, each carrying when a maintainer
	/// last replied to it.
	/// </summary>
	public List<RepositoryIssue> OpenIssues { get; set; } = [];

	/// <summary>
	/// The open items nobody has answered for at least a week — the ones that count as failures.
	/// </summary>
	/// <remarks>
	/// Evaluated against the clock on each read rather than stored, so a row restored from a cache
	/// written yesterday reports today's staleness. That is also why this is not a cached count.
	/// </remarks>
	public IEnumerable<RepositoryIssue> StaleIssues
		=> OpenIssues.Where(issue => issue.SeverityAt(DateTimeOffset.UtcNow)
			is AssessmentSeverity.Error or AssessmentSeverity.Critical);
```

Then replace the four total properties:

```csharp
	/// <summary>
	/// Total number of failures: failing rules, plus every open issue or pull request that has gone
	/// unanswered for a week or more.
	/// </summary>
	/// <remarks>
	/// Fresh issues are deliberately excluded. An issue answered yesterday is not a failure, and
	/// counting it as one would mean a healthy, responsive repository could never reach zero — which
	/// would destroy the meaning of every figure on the dashboard.
	/// </remarks>
	public int TotalFailures
		=> (Assessment?.FailedCount ?? 0)
			+ StaleIssues.Count();

	/// <summary>
	/// Total number of critical findings, including issues unanswered for a month or more.
	/// </summary>
	public int TotalCriticals
		=> (Assessment?.CriticalCount ?? 0)
			+ OpenIssues.Count(i => i.SeverityAt(DateTimeOffset.UtcNow) == AssessmentSeverity.Critical);

	/// <summary>
	/// Total number of errors, including issues unanswered for between a week and a month.
	/// </summary>
	public int TotalErrors
		=> (Assessment?.ErrorCount ?? 0)
			+ OpenIssues.Count(i => i.SeverityAt(DateTimeOffset.UtcNow) == AssessmentSeverity.Error);
```

`TotalWarnings` is left exactly as it is — the bands never produce a Warning.

Add `using PanoramicData.NugetManagement.Models;` at the top if it is not already present (it is — `RepoAssessment` comes from there).

- [ ] **Step 4: Bump the cache discovery version**

In `PanoramicData.NugetManagement.Web/Services/DashboardCacheService.cs`, change the constant and extend its remarks list:

```csharp
	/// 4: rows carry their repository's open issues and pull requests.
	public const int DiscoveryVersion = 4;
```

Keep the existing numbered lines 1–3 above the new line.

- [ ] **Step 5: Clear issues when a repository stops being governed**

In `PanoramicData.NugetManagement.Web/Services/GovernanceScope.cs`, immediately after `row.Assessment = null;`, add:

```csharp
		// Issues count toward the same totals as rules, so leaving them would keep somebody else's
		// repository contributing failures after it stopped being ours.
		row.OpenIssues = [];
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet build PanoramicData.NugetManagement.slnx -c Debug
cd PanoramicData.NugetManagement.Test/bin/Debug/net10.0 && ./PanoramicData.NugetManagement.Test.exe
```

Expected: PASS, the whole suite, now 584 tests. `DashboardCacheVersionTests` compares against the constant so the bump needs no test change; `ExcludedRepositoryRollupTests` and `PackageNodeHealthTests` exercise the totals and must still pass.

- [ ] **Step 7: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Models/RepositoryDashboardRow.cs PanoramicData.NugetManagement.Web/Services/DashboardCacheService.cs PanoramicData.NugetManagement.Web/Services/GovernanceScope.cs PanoramicData.NugetManagement.Test/RepositoryIssueRollupTests.cs
git commit -m "Count unanswered issues as repository failures"
```

---

### Task 5: Fetching the issues during assessment

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Services/DashboardService.cs:291-366` (fetch inside `AssessRepositoryAsync`)

**Interfaces:**
- Consumes: `RepositoryIssueService` and `OctokitGitHubIssueApi` from Tasks 2 and 3; `RepositoryDashboardRow.OpenIssues` from Task 4.
- Produces: nothing new. `AssessRepositoryAsync` keeps its existing signature.

- [ ] **Step 1: Fetch the issues alongside the rules**

In `DashboardService.AssessRepositoryAsync`, after `row.CategorySummaries = BuildCategorySummaries(results);` and before `row.Status = PackageStatus.Assessed;`, insert:

```csharp
			// Fetched here rather than on its own schedule so there is one refresh path, one cache and
			// one staleness window. A failure to read the inbox must not fail the assessment: the rules
			// have already been evaluated by this point, and losing them because a comment endpoint
			// misbehaved would be a poor trade.
			try
			{
				var issueService = new RepositoryIssueService(new OctokitGitHubIssueApi(github));
				row.OpenIssues = [.. await issueService
					.GetOpenIssuesAsync(parts[0], parts[1], cancellationToken)
					.ConfigureAwait(false)];
			}
			catch (ApiException ex)
			{
				_logger.LogWarning(
					ex,
					"Could not read open issues for {Repo}; its inbox will show as empty.",
					row.RepositoryFullName);
				row.OpenIssues = [];
			}
```

`row.StatusMessage = $"{row.TotalFailures} issue(s) found.";` on the line below now naturally includes the stale items, which is correct.

- [ ] **Step 2: Build and run the full suite**

```bash
dotnet build PanoramicData.NugetManagement.slnx -c Debug
cd PanoramicData.NugetManagement.Test/bin/Debug/net10.0 && ./PanoramicData.NugetManagement.Test.exe
```

Expected: PASS, 584 tests. No new tests here — this is wiring between two tested units and a live API, and a test of it would only assert that one line calls another.

- [ ] **Step 3: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Services/DashboardService.cs
git commit -m "Read each repository's open issues while assessing it"
```

---

### Task 6: The `Issues` node in the tree

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Models/NavItem.cs` (add `IssueNumber`)
- Modify: `PanoramicData.NugetManagement.Web/Services/NavTreeDataProvider.cs:56-80` (two new key builders, and `RepositoryFromKey` must recognise them)
- Modify: `PanoramicData.NugetManagement.Web/Services/NavTreeDataProvider.cs:410-505` (the node itself, the leaves, and the category `SortOrder`)
- Test: `PanoramicData.NugetManagement.Test/RepositoryIssueNavNodeTests.cs`

**Interfaces:**
- Consumes: `RepositoryIssue` from Task 1, `RepositoryDashboardRow.OpenIssues` from Task 4, `NavHealthRollup.Worst`/`FromSeverity`/`Icon` (existing).
- Produces: `NavTreeDataProvider.RepoIssuesKey(string repositoryFullName)` → `"repoissues:{full}"`, `NavTreeDataProvider.RepoIssueKey(string repositoryFullName, int number)` → `"repoissue:{full}:{number}"`, `NavItem.IssueNumber` (`int?`), `NavView.RepositoryIssueDetail` (added in Task 7).

- [ ] **Step 1: Write the failing tests**

Create `PanoramicData.NugetManagement.Test/RepositoryIssueNavNodeTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests the repository-level Issues branch of the navigation tree.
/// </summary>
public class RepositoryIssueNavNodeTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private const string Repo = "panoramicdata/Sample";

	private readonly string _cacheDirectory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	private static RepositoryIssue Aged(int number, int daysSinceReply, bool isPullRequest = false)
		=> new()
		{
			Number = number,
			Title = $"Item {number}",
			IsPullRequest = isPullRequest,
			HtmlUrl = $"https://github.com/{Repo}/issues/{number}",
			AuthorLogin = "reporter",
			CreatedAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromDays(400),
			LastMaintainerReplyUtc = DateTimeOffset.UtcNow - TimeSpan.FromDays(daysSinceReply)
		};

	/// <summary>
	/// Builds the whole navigation tree over a single assessed repository carrying the given open
	/// items. The assessment holds one failing rule so that a category node exists to sort against.
	/// </summary>
	private List<NavItem> Tree(params RepositoryIssue[] issues)
	{
		var rows = new List<RepositoryDashboardRow>
		{
			new()
			{
				Organization = "panoramicdata",
				RepositoryFullName = Repo,
				Packages = [new() { PackageId = "Sample" }],
				OpenIssues = [.. issues],
				Assessment = new RepoAssessment
				{
					RepositoryFullName = Repo,
					DefaultBranch = "main",
					AssessedAtUtc = DateTimeOffset.UtcNow,
					RuleResults =
					[
						new RuleResult
						{
							RuleId = "PKG-01",
							RuleName = "Package id set",
							Category = AssessmentCategory.ProjectMetadata,
							Severity = AssessmentSeverity.Error,
							Passed = false,
							Message = "missing"
						}
					]
				},
				CategorySummaries = new Dictionary<AssessmentCategory, CategorySummary>
				{
					[AssessmentCategory.ProjectMetadata] = new()
				}
			}
		};

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

	[Fact]
	public void TheIssuesNodeCountsBothKindsIncludingHealthyOnes()
	{
		var node = Tree(Aged(1, 1), Aged(2, 40), Aged(3, 2, isPullRequest: true))
			.Single(i => i.Key == NavTreeDataProvider.RepoIssuesKey(Repo));

		node.Text.Should().Be("Issues (3)");
	}

	[Fact]
	public void TheIssuesNodeTakesTheWorstSeverityBeneathIt()
	{
		var node = Tree(Aged(1, 1), Aged(2, 40))
			.Single(i => i.Key == NavTreeDataProvider.RepoIssuesKey(Repo));

		node.HealthStatus.Should().Be(PackageHealthStatus.Error);
		node.IconCss.Should().Contain("text-danger");
	}

	[Fact]
	public void ARepositoryWithNothingOpenShowsAnEmptyLeaf()
	{
		var node = Tree().Single(i => i.Key == NavTreeDataProvider.RepoIssuesKey(Repo));

		node.Text.Should().Be("Issues (0)");
		node.IsLeaf.Should().BeTrue();
	}

	[Fact]
	public void EachItemBecomesALeafUnderTheNode()
	{
		var leaves = Tree(Aged(1, 1), Aged(2, 40))
			.Where(i => i.ParentKey == NavTreeDataProvider.RepoIssuesKey(Repo))
			.ToList();

		leaves.Should().HaveCount(2);
		leaves.Should().AllSatisfy(l => l.IsLeaf.Should().BeTrue());
		leaves.Select(l => l.IssueNumber).Should().BeEquivalentTo([1, 2]);
	}

	[Fact]
	public void AnIssueAndAPullRequestCarryDifferentGlyphs()
	{
		var leaves = Tree(Aged(1, 1), Aged(2, 1, isPullRequest: true))
			.Where(i => i.ParentKey == NavTreeDataProvider.RepoIssuesKey(Repo))
			.ToDictionary(l => l.IssueNumber!.Value);

		leaves[1].IconCss.Should().Contain("fa-circle-dot");
		leaves[2].IconCss.Should().Contain("fa-code-pull-request");
	}

	[Fact]
	public void LeavesSortWorstFirstAndInterleaveTheTwoKinds()
	{
		var ordered = Tree(
				Aged(1, 1),
				Aged(2, 40, isPullRequest: true),
				Aged(3, 10))
			.Where(i => i.ParentKey == NavTreeDataProvider.RepoIssuesKey(Repo))
			.OrderBy(i => i.SortOrder)
			.Select(i => i.IssueNumber)
			.ToList();

		ordered.Should().Equal([2, 3, 1],
			"critical first, then the error, then the fresh one, whatever kind each is");
	}

	[Fact]
	public void TheIssuesNodeSortsAbovTheCategories()
	{
		var tree = Tree(Aged(1, 1));
		var issues = tree.Single(i => i.Key == NavTreeDataProvider.RepoIssuesKey(Repo));

		issues.SortOrder.Should().Be(1);
		tree.Where(i => i.View == NavView.CategoryDetail)
			.Should().AllSatisfy(c => c.SortOrder.Should().Be(2));
	}

	[Fact]
	public void ALeafResolvesBackToItsRepository()
	{
		var leaf = Tree(Aged(7, 1))
			.Single(i => i.ParentKey == NavTreeDataProvider.RepoIssuesKey(Repo));

		leaf.Key.Should().Be(NavTreeDataProvider.RepoIssueKey(Repo, 7));
		NavTreeDataProvider.RepositoryFromKey(leaf.Key).Should().Be(Repo);
	}
}
```

The fixture is the one `NotGovernedNavNodeTests` uses — a real `DashboardCacheService` over a temporary file, seeded with `SetRows`, then a real `NavTreeDataProvider`. If `CategorySummary` needs constructor arguments this repository's version does not default, copy the shape from `PackageNodeHealthTests`.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet build PanoramicData.NugetManagement.slnx -c Debug
```

Expected: FAIL to compile, with `CS0117` for `RepoIssuesKey` and `CS1061` for `NavItem.IssueNumber`.

- [ ] **Step 3: Add the key builders and teach `RepositoryFromKey` about them**

In `NavTreeDataProvider.cs`, after `PackagesKey`:

```csharp
	/// <summary>Builds the key for a repository's GitHub "Issues" container.</summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <remarks>
	/// Prefixed "repoissues" rather than "issues": <see cref="IssuesKey"/> already owns that prefix
	/// for the organisation's rule-failure branch, and PDTree throws on a duplicate key and swallows
	/// the exception, rendering the whole tree empty with nothing in the console.
	/// </remarks>
	public static string RepoIssuesKey(string repositoryFullName) => $"repoissues:{repositoryFullName}";

	/// <summary>Builds the key for one open issue or pull request of a repository.</summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <param name="number">The issue or pull request number.</param>
	public static string RepoIssueKey(string repositoryFullName, int number)
		=> $"repoissue:{repositoryFullName}:{number}";
```

And in `RepositoryFromKey`, extend the recognised prefixes:

```csharp
		if (prefix is not ("repo" or "pkgs" or "pkg" or "cat" or "rule" or "repoissues" or "repoissue"))
```

- [ ] **Step 4: Add `IssueNumber` to `NavItem`**

In `PanoramicData.NugetManagement.Web/Models/NavItem.cs`, after `RuleId`:

```csharp
	/// <summary>
	/// For an open issue or pull request leaf, its GitHub number. Lets a selection be resolved back
	/// to its <see cref="PanoramicData.NugetManagement.Models.RepositoryIssue"/> without parsing the
	/// key.
	/// </summary>
	public int? IssueNumber { get; init; }
```

- [ ] **Step 5: Build the nodes**

In `NavTreeDataProvider.cs`, immediately after the `foreach` that adds the package nodes and before the `if (row.Assessment is null)` guard, insert:

```csharp
			// Open issues and pull requests. One branch for both kinds, because GitHub's own model
			// treats a pull request as an issue and because the reader wants the thing that has gone
			// unanswered longest, whichever kind it happens to be. The leaf glyph says which.
			var repoIssuesKey = RepoIssuesKey(row.RepositoryFullName);
			var nowUtc = DateTimeOffset.UtcNow;

			var issueStatus = NavHealthRollup.Worst(
				row.OpenIssues.Select(issue => NavHealthRollup.FromSeverity(issue.SeverityAt(nowUtc))));

			items.Add(new NavItem
			{
				Key = repoIssuesKey,
				// The count is every open item, healthy ones included: it answers "what is in this
				// inbox". The repository's IssueCount, which counts only the unanswered, is a
				// different question and deliberately a different number.
				Text = $"Issues ({row.OpenIssues.Count})",
				ParentKey = repoKey,
				IconCss = NavHealthRollup.Icon("fas fa-comments", issueStatus),
				HealthStatus = issueStatus,
				View = NavView.None,
				Organization = organization,
				RepositoryFullName = row.RepositoryFullName,
				IsLeaf = row.OpenIssues.Count == 0,
				SortOrder = 1
			});

			foreach (var issue in row.OpenIssues)
			{
				var severity = issue.SeverityAt(nowUtc);
				var severityRank = severity switch
				{
					AssessmentSeverity.Critical => 0,
					AssessmentSeverity.Error => 1,
					_ => 2
				};

				items.Add(new NavItem
				{
					Key = RepoIssueKey(row.RepositoryFullName, issue.Number),
					Text = $"#{issue.Number} {issue.Title}",
					ParentKey = repoIssuesKey,
					IconCss = NavHealthRollup.Icon(
						issue.IsPullRequest ? "fas fa-code-pull-request" : "fas fa-circle-dot",
						NavHealthRollup.FromSeverity(severity)),
					HealthStatus = NavHealthRollup.FromSeverity(severity),
					View = NavView.RepositoryIssueDetail,
					Organization = organization,
					RepositoryFullName = row.RepositoryFullName,
					IssueNumber = issue.Number,
					IsLeaf = true,
					// Worst first, then oldest first within a band. PDTree breaks SortOrder ties on
					// Text, and alphabetical order on "#1000" against "#99" is meaningless, so the
					// rank has to carry the number rather than leave it to the tie-break.
					SortOrder = (severityRank * 1_000_000) + Math.Min(issue.Number, 999_999),
					IssueCount = severity is AssessmentSeverity.Critical or AssessmentSeverity.Error ? 1 : 0,
					HasErrors = severity is AssessmentSeverity.Critical or AssessmentSeverity.Error
				});
			}
```

Then change the category node's `SortOrder = 1,` to `SortOrder = 2,` so the categories still sit below.

`NavView.RepositoryIssueDetail` does not exist yet — add it now to `NavItem.cs`'s `NavView` enum so this task compiles, and Task 7 gives it a renderer:

```csharp
	/// <summary>
	/// One open GitHub issue or pull request of a repository: who raised it, when a maintainer last
	/// replied, and how stale that makes it.
	/// </summary>
	RepositoryIssueDetail
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet build PanoramicData.NugetManagement.slnx -c Debug
cd PanoramicData.NugetManagement.Test/bin/Debug/net10.0 && ./PanoramicData.NugetManagement.Test.exe --filter-method '*RepositoryIssueNavNodeTests*'
```

Expected: PASS, 8 tests.

Then the full suite:

```bash
./PanoramicData.NugetManagement.Test.exe
```

Expected: `NavViewCoverageTests.EveryViewTheTreeCanSelectShouldBeRenderedSomewhere` FAILS, because `RepositoryIssueDetail` is now selectable with no case in the render switch. That is the correct failure and Task 7 fixes it. Every other test passes.

- [ ] **Step 7: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Models/NavItem.cs PanoramicData.NugetManagement.Web/Services/NavTreeDataProvider.cs PanoramicData.NugetManagement.Test/RepositoryIssueNavNodeTests.cs
git commit -m "Show open issues and pull requests under each repository"
```

---

### Task 7: The issue detail view

Closes the `NavViewCoverageTests` failure Task 6 opened.

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Components/RepositoryIssueView.razor`
- Modify: `PanoramicData.NugetManagement.Web/Components/Pages/Home.razor:2928-2979` (a case in `RenderCurrentView`)
- Modify: `PanoramicData.NugetManagement.Web/Components/Pages/Home.razor` (record the selected issue number when a node is selected)

**Interfaces:**
- Consumes: `NavView.RepositoryIssueDetail` and `NavItem.IssueNumber` from Task 6, `RepositoryDashboardRow.OpenIssues` from Task 4.
- Produces: `RepositoryIssueView` component with parameters `Issue` (`RepositoryIssue?`) and `RepositoryFullName` (`string?`).

A new component rather than another render method: `Home.razor` is already about six thousand lines, and `IssuesView.razor` sets the precedent for a panel that lives on its own.

- [ ] **Step 1: Write the component**

Create `PanoramicData.NugetManagement.Web/Components/RepositoryIssueView.razor`:

```razor
@using PanoramicData.NugetManagement.Models

@if (Issue is null)
{
	<div class="alert alert-secondary">
		This issue is no longer in the cache. Re-assess the repository to refresh its inbox.
	</div>
}
else
{
	<div class="mb-3">
		<h4>
			<i class="@(Issue.IsPullRequest ? "fas fa-code-pull-request" : "fas fa-circle-dot") @SeverityColour"></i>
			#@Issue.Number @Issue.Title
		</h4>
		<div class="text-muted">@(Issue.IsPullRequest ? "Pull request" : "Issue") in @RepositoryFullName</div>
	</div>

	<dl class="row">
		<dt class="col-sm-3">Raised by</dt>
		<dd class="col-sm-9">@Issue.AuthorLogin</dd>

		<dt class="col-sm-3">Opened</dt>
		<dd class="col-sm-9">@Issue.CreatedAtUtc.ToString("u")</dd>

		<dt class="col-sm-3">Last maintainer reply</dt>
		<dd class="col-sm-9">
			@if (Issue.LastMaintainerReplyUtc is null)
			{
				<span class="text-danger">Never — nobody with write access has commented.</span>
			}
			else
			{
				@Issue.LastMaintainerReplyUtc.Value.ToString("u")
			}
		</dd>

		<dt class="col-sm-3">Waiting</dt>
		<dd class="col-sm-9">@WholeDaysWaiting day(s)</dd>

		<dt class="col-sm-3">Severity</dt>
		<dd class="col-sm-9"><span class="@SeverityColour">@Severity</span></dd>
	</dl>

	<a class="btn btn-outline-primary" href="@Issue.HtmlUrl" target="_blank" rel="noopener">
		<i class="fab fa-github"></i> Open on GitHub
	</a>
}

@code {
	/// <summary>The issue or pull request to show. Null when it has fallen out of the cache.</summary>
	[Parameter]
	public RepositoryIssue? Issue { get; set; }

	/// <summary>The repository the item belongs to, as "owner/name".</summary>
	[Parameter]
	public string? RepositoryFullName { get; set; }

	private AssessmentSeverity Severity
		=> Issue?.SeverityAt(DateTimeOffset.UtcNow) ?? AssessmentSeverity.Info;

	private int WholeDaysWaiting
		=> Issue is null ? 0 : (int)(DateTimeOffset.UtcNow - Issue.ClockStartUtc).TotalDays;

	private string SeverityColour => Severity switch
	{
		AssessmentSeverity.Critical or AssessmentSeverity.Error => "text-danger",
		AssessmentSeverity.Warning => "text-warning",
		_ => "text-info"
	};
}
```

- [ ] **Step 2: Record the selection in `Home.razor`**

In the node-selection handler at `Home.razor:1134-1140`, alongside `_selectedRuleId = item.RuleId;`, add:

```csharp
		_selectedIssueNumber = item.IssueNumber;
```

And declare the field beside `_selectedRuleId` at `Home.razor:883`:

```csharp
	private int? _selectedIssueNumber;
```

No change is needed to `IsSelectableNavNode` — it excludes only the `repos:` and `repos-loading:` prefixes, so the new leaves are selectable already.

- [ ] **Step 3: Render it**

In `RenderCurrentView`, immediately before the `default:` case:

```razor
			case NavView.RepositoryIssueDetail:
				@* Its own component rather than another render method here: this file is already
				   enormous, and IssuesView sets the precedent for a self-contained panel. *@
				<RepositoryIssueView Issue="@SelectedRepositoryIssue" RepositoryFullName="@_selectedRow?.RepositoryFullName" />
				break;
```

Add the lookup beside the other selection helpers in `@code`. The handler already resolves and stores the selected repository in `_selectedRow`, so the issue is found on that rather than by searching the rows again:

```csharp
	/// <summary>
	/// The selected open issue, taken from the selected row. Null if the cache has been refreshed
	/// since the node was drawn and the item has closed.
	/// </summary>
	private RepositoryIssue? SelectedRepositoryIssue
		=> _selectedIssueNumber is null
			? null
			: _selectedRow?.OpenIssues.FirstOrDefault(i => i.Number == _selectedIssueNumber);
```

`Home.razor` may need `@using PanoramicData.NugetManagement.Models` — check whether `_Imports.razor` already supplies it before adding one.

- [ ] **Step 4: Run the full suite**

```bash
dotnet build PanoramicData.NugetManagement.slnx -c Debug
cd PanoramicData.NugetManagement.Test/bin/Debug/net10.0 && ./PanoramicData.NugetManagement.Test.exe
```

Expected: PASS, 592 tests, including `NavViewCoverageTests`, which now finds `case NavView.RepositoryIssueDetail:` in the switch.

- [ ] **Step 5: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Components/RepositoryIssueView.razor PanoramicData.NugetManagement.Web/Components/Pages/Home.razor
git commit -m "Show the detail of a selected issue or pull request"
```

---

### Task 8: See it working against the real estate

The sweep, the adapter and the rollup are each tested in isolation; nothing so far has proved that Octokit returns what the adapter thinks it does. This is the only step that does.

**Files:** none — this is a verification task.

- [ ] **Step 1: Start the application**

```bash
dotnet run --project PanoramicData.NugetManagement.Web
```

- [ ] **Step 2: Assess a repository known to have open issues and check the tree**

Pick a repository in the configured organisation with several open items, including at least one Dependabot pull request, and press Re-assess. Confirm:

- An `Issues (N)` node appears under it, with `N` matching the open count on github.com (issues plus pull requests).
- Issue leaves show `fa-circle-dot`, pull request leaves `fa-code-pull-request`.
- A long-neglected item is red and sorts to the top; a recently answered one is blue and sorts to the bottom.
- The repository node's own count has grown by the number of stale items.
- Selecting a leaf shows the detail panel, and the "last maintainer reply" agrees with the last comment by a maintainer on github.com.

- [ ] **Step 3: Confirm the reporter-chase case**

Find or create an issue where the newest comment is from a non-maintainer and the last maintainer comment is over a month old. It must read `Critical`. This is the case the whole design exists for, and no unit test can prove GitHub reports the association the adapter expects.

- [ ] **Step 4: Note the cost**

Watch the console for rate-limit warnings during a full estate refresh. If the sweep proves expensive on a repository with a very long comment history, the lever is `RepositoryIssueService.MaxSweepPages`; record what you saw rather than tuning it speculatively.

- [ ] **Step 5: Commit anything the run corrected**

If Step 2 or 3 exposed a mismatch in the adapter, fix it, re-run the suite, and commit. If nothing needed changing, there is nothing to commit — say so.
