using Microsoft.Extensions.Time.Testing;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that triage teaches <see cref="ActionVersionCatalog"/> the versions Dependabot proposes.
/// </summary>
/// <remarks>
/// The floor was learned only from repositories already using a version — "a single repository ahead
/// of the pack is the canary" — so an action nobody had upgraded yet could never have a floor above
/// what everybody used, and CI-12 could never fail for it. SolarWinds.Api sat on
/// actions/deploy-pages v4 with Dependabot offering v5 for eleven days: the rule governed the action,
/// read the workflow, and passed, so triage reported "no auto-fix" about something it could have
/// fixed the moment it knew v5 existed. Dependabot is the only part of this system that hears from
/// upstream, and its proposal is now an observation like any other.
/// </remarks>
[Collection(ActionVersionCatalogCollection.Name)]
public class DependabotTeachesActionFloorTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void RaisesTheFloor_WhenAnActionBumpProposesMoreThanWeHaveSeen()
	{
		var catalog = InMemoryCatalog();

		Triage(
			PullRequest(34, "Bump actions/deploy-pages from 4 to 5"),
			Ctx(Workflow("actions/deploy-pages@v4")),
			catalog);

		catalog.RecentBumps.Should().ContainSingle()
			.Which.Should().BeEquivalentTo(new
			{
				Action = "actions/deploy-pages",
				From = "v0",
				To = "v5"
			});
	}

	[Fact]
	public void AttributesTheRaise_ToTheRepositoryWhoseProposalTaughtIt()
	{
		var catalog = InMemoryCatalog();

		Triage(
			PullRequest(34, "Bump actions/deploy-pages from 4 to 5"),
			Ctx(Workflow("actions/deploy-pages@v4")),
			catalog);

		catalog.RecentBumps.Should().ContainSingle()
			.Which.Repository.Should().Be("panoramicdata/Athonet.Api");
	}

	[Fact]
	public void LeavesTheFloorAlone_WhenTheProposalIsNoHigherThanTheFloor()
	{
		// Dependabot can be behind: a pull request raised before another repository moved first.
		var catalog = InMemoryCatalog(("actions/deploy-pages", 5));

		Triage(
			PullRequest(34, "Bump actions/deploy-pages from 3 to 5"),
			Ctx(Workflow("actions/deploy-pages@v3")),
			catalog);

		catalog.RecentBumps.Should().ContainSingle("the seeded floor is the only bump recorded");
	}

	[Fact]
	public void LeavesTheFloorAlone_ForANuGetBump()
	{
		// The catalog holds GitHub Actions. A package id recorded in it would be read as an action
		// forever after, and every repository held to a floor for something that is not one.
		// Deliberately a package whose version reads as a bare major, so nothing but the catalog's own
		// "is this used as an action here" test stands between it and being learned.
		var catalog = InMemoryCatalog();

		Triage(
			PullRequest(43, "Bump Serilog from 3 to 4"),
			Ctx(Packages("Serilog", "3.0.0"), Workflow("actions/checkout@v7")),
			catalog);

		catalog.RecentBumps.Should().BeEmpty();
	}

	[Fact]
	public void TeachesOnlyTheActionsThisRepositoryUses_InAMixedGroup()
	{
		// A group where one action is used here and one has been dropped. The group is not obsolete —
		// half of it is still real — so the teaching runs, and it must still refuse the half this
		// repository knows nothing about: a floor learned from a repository that had stopped using an
		// action would hold every other repository to it.
		var catalog = InMemoryCatalog();

		Triage(
			Grouped(36, ("actions/configure-pages", "5", "6"), ("actions/cache", "3", "4")),
			Ctx(Workflow("actions/configure-pages@v5")),
			catalog);

		catalog.RecentBumps.Select(bump => bump.Action)
			.Should().BeEquivalentTo(["actions/configure-pages"]);
	}

	[Fact]
	public void LeavesTheFloorAlone_WhenTheRepositoryAlreadyMeetsTheProposal()
	{
		// Nothing to learn: the repository is already at the proposed version, so whatever raised the
		// floor to that point has already happened.
		var catalog = InMemoryCatalog();

		var triage = Triage(
			PullRequest(34, "Bump actions/deploy-pages from 4 to 5"),
			Ctx(Workflow("actions/deploy-pages@v5")),
			catalog);

		triage.Verdict.Should().Be(DependabotVerdict.AlreadySatisfied);
		catalog.RecentBumps.Should().BeEmpty();
	}

	[Fact]
	public void LeavesTheFloorAlone_WhenTheActionIsNotUsedHere()
	{
		// A pull request about an action this repository has dropped says nothing about the version
		// the estate should hold: it is closed as obsolete, and a floor raised from it would hold
		// every other repository to a version learned from one that had stopped caring.
		var catalog = InMemoryCatalog();

		var triage = Triage(
			PullRequest(34, "Bump actions/deploy-pages from 4 to 5"),
			Ctx(Workflow("actions/checkout@v7")),
			catalog);

		triage.Verdict.Should().Be(DependabotVerdict.Obsolete);
		catalog.RecentBumps.Should().BeEmpty();
	}

	[Fact]
	public void TeachesEveryOutstandingBump_InAGroupedPullRequest()
	{
		var catalog = InMemoryCatalog();

		Triage(
			Grouped(35, ("actions/configure-pages", "5", "6"), ("actions/upload-pages-artifact", "3", "5")),
			Ctx(Workflow("actions/configure-pages@v5", "actions/upload-pages-artifact@v3")),
			catalog);

		catalog.RecentBumps.Select(bump => $"{bump.Action}:{bump.To}")
			.Should().BeEquivalentTo(["actions/configure-pages:v6", "actions/upload-pages-artifact:v5"]);
	}

	[Fact]
	public void TeachesNothing_FromAPullRequestThatIsNotDependabots()
	{
		// A person's pull request titled like a bump is not a statement about what upstream offers.
		var catalog = InMemoryCatalog();

		Triage(
			PullRequest(34, "Bump actions/deploy-pages from 4 to 5", author: "davidnmbond"),
			Ctx(Workflow("actions/deploy-pages@v4")),
			catalog);

		catalog.RecentBumps.Should().BeEmpty();
	}

	private static ActionVersionCatalog InMemoryCatalog(params (string Action, int Major)[] floors)
	{
		// Null path: nothing is persisted, so a test can never rewrite the committed catalog.
		var catalog = new ActionVersionCatalog(null);
		foreach (var (action, major) in floors)
		{
			catalog.Observe(action, major, "v0", "seed");
		}

		return catalog;
	}

	private static DependabotTriage Triage(
		RepositoryIssue issue,
		RepositoryContext context,
		ActionVersionCatalog catalog)
		=> new DependabotTriageService(
				RuleRegistry.Rules,
				new FakeTimeProvider(issue.CreatedAtUtc + TimeSpan.FromDays(1)),
				catalog)
			.Triage([issue], context, [], _ => true)
			.Should().ContainSingle().Subject;

	private static RepositoryIssue PullRequest(int number, string title, string author = "dependabot[bot]")
		=> new()
		{
			Number = number,
			Title = title,
			IsPullRequest = true,
			HtmlUrl = $"https://github.com/panoramicdata/Athonet.Api/pull/{number}",
			AuthorLogin = author,
			CreatedAtUtc = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)
		};

	private static RepositoryIssue Grouped(int number, params (string Action, string From, string To)[] bumps)
		=> new()
		{
			Number = number,
			Title = $"Bump the github-actions group with {bumps.Length} updates",
			IsPullRequest = true,
			HtmlUrl = $"https://github.com/panoramicdata/Athonet.Api/pull/{number}",
			AuthorLogin = "dependabot[bot]",
			CreatedAtUtc = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
			Body = string.Join(
				"\n",
				bumps.Select(b =>
					$"Updated [{b.Action}](https://github.com/{b.Action}) from {b.From} to {b.To}."))
		};

	private static RepositoryContext Ctx(params (string Path, string Content)[] files) => new()
	{
		FullName = "panoramicdata/Athonet.Api",
		Name = "Athonet.Api",
		DefaultBranch = "main",
		CurrentBranch = "main",
		Options = new RepoOptions(),
		FilePaths = [.. files.Select(f => f.Path)],
		FileContents = files.ToDictionary(f => f.Path, f => f.Content, StringComparer.OrdinalIgnoreCase)
	};

	private static (string, string) Workflow(params string[] uses)
		=> (".github/workflows/ci.yml",
			"jobs:\n  build:\n    steps:\n" + string.Concat(uses.Select(u => $"    - uses: {u}\n")));

	private static (string, string) Packages(string packageId, string version)
		=> ("Directory.Packages.props",
			$"""<Project><ItemGroup><PackageVersion Include="{packageId}" Version="{version}" /></ItemGroup></Project>""");
}
