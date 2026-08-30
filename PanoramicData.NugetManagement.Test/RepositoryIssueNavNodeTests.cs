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

	/// <summary>
	/// Builds the whole navigation tree over a single repository with no <c>OpenIssues</c>, varying
	/// independently whether it was assessed and whether its inbox was actually read — the situations
	/// that "Issues (0)" must not render identically.
	/// </summary>
	/// <param name="assessed">Whether the row carries an assessment.</param>
	/// <param name="inboxRead">Whether the inbox was successfully read.</param>
	private List<NavItem> TreeWithNoOpenIssues(bool assessed, bool inboxRead)
	{
		var rows = new List<RepositoryDashboardRow>
		{
			new()
			{
				Organization = "panoramicdata",
				RepositoryFullName = Repo,
				Packages = [new() { PackageId = "Sample" }],
				OpenIssues = [],
				OpenIssuesKnown = inboxRead,
				Assessment = assessed
					? new RepoAssessment
					{
						RepositoryFullName = Repo,
						DefaultBranch = "main",
						AssessedAtUtc = DateTimeOffset.UtcNow,
						RuleResults = []
					}
					: null
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

	[Fact]
	public void ARepositoryWhoseInboxWasReadAndIsEmptyIsGreenNotGrey()
	{
		var node = TreeWithNoOpenIssues(assessed: true, inboxRead: true)
			.Single(i => i.Key == NavTreeDataProvider.RepoIssuesKey(Repo));

		node.HealthStatus.Should().Be(PackageHealthStatus.Success,
			"the inbox was read and is genuinely empty");
	}

	[Fact]
	public void AnUnassessedRepositoryWithNothingOpenStaysUnknown()
	{
		var node = TreeWithNoOpenIssues(assessed: false, inboxRead: false)
			.Single(i => i.Key == NavTreeDataProvider.RepoIssuesKey(Repo));

		node.HealthStatus.Should().Be(PackageHealthStatus.Unknown,
			"nothing open is not yet known to mean nothing to worry about, because nothing has been fetched");
	}

	[Fact]
	public void AnAssessedRepositoryWhoseInboxWasNotReadStaysUnknown()
	{
		var node = TreeWithNoOpenIssues(assessed: true, inboxRead: false)
			.Single(i => i.Key == NavTreeDataProvider.RepoIssuesKey(Repo));

		node.HealthStatus.Should().Be(PackageHealthStatus.Unknown,
			"a local assessment with no GitHub client, and a fetch that failed and was swallowed, both "
			+ "leave an assessed row with an empty list that nobody has actually read");
	}

	[Fact]
	public void TheIssuesNodeIsAContainerHeadingLikePackages()
		=> NavTreeDataProvider.IsContainerNode(
			Tree(Aged(1, 1)).Single(i => i.Key == NavTreeDataProvider.RepoIssuesKey(Repo)))
			.Should().BeTrue("it is per-repository, expandable and parenthesis-counted, exactly like Packages");

	[Fact]
	public void AnIndividualIssueLeafIsNotAContainerHeading()
		=> NavTreeDataProvider.IsContainerNode(
			Tree(Aged(1, 1)).Single(i => i.ParentKey == NavTreeDataProvider.RepoIssuesKey(Repo)))
			.Should().BeFalse("a leaf issue is an item, not a heading");
}
