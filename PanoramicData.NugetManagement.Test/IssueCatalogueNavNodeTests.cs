using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that the organisation's Issues branch lists the whole rule catalogue once anything has been
/// assessed — a rule nothing fails is still shown, green — rather than only the rules with failures.
/// </summary>
public class IssueCatalogueNavNodeTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private const string _repo = "panoramicdata/Sample";

	/// <summary>A real rule id and its real category, so the tree places it where the registry does.</summary>
	private const string _failingRuleId = "META-01";

	private const AssessmentCategory _failingCategory = AssessmentCategory.ProjectMetadata;

	private readonly string _cacheDirectory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			Directory.Delete(_cacheDirectory, recursive: true);
		}
		catch (IOException)
		{
			// A locked temp file must not fail the test that produced it.
		}

		GC.SuppressFinalize(this);
	}

	/// <summary>
	/// Builds the whole navigation tree over one repository, assessed or not. When assessed it holds a
	/// single failing rule, so the branch has both a failure to colour and a catalogue to fill in.
	/// </summary>
	private List<NavItem> Tree(bool assessed)
	{
		var rows = new List<RepositoryDashboardRow>
		{
			new()
			{
				Organization = "panoramicdata",
				RepositoryFullName = _repo,
				Packages = [new() { PackageId = "Sample" }],
				OpenIssues = [],
				Assessment = assessed
					? new RepoAssessment
					{
						RepositoryFullName = _repo,
						DefaultBranch = "main",
						AssessedAtUtc = DateTimeOffset.UtcNow,
						RuleResults =
						[
							new RuleResult
							{
								RuleId = _failingRuleId,
								RuleName = "PackageId is set",
								Category = _failingCategory,
								Severity = AssessmentSeverity.Error,
								Passed = false,
								Message = "missing"
							}
						]
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

	private static List<NavItem> RuleNodes(List<NavItem> tree)
		=> [.. tree.Where(item => item.Key.StartsWith("irule:", StringComparison.Ordinal))];

	private static List<NavItem> CategoryNodes(List<NavItem> tree)
		=> [.. tree.Where(item => item.Key.StartsWith("icat:", StringComparison.Ordinal))];

	[Fact]
	public void EveryRuleInTheRegistry_AppearsUnderIssues()
	{
		var listed = RuleNodes(Tree(assessed: true)).Select(item => item.RuleId).ToList();

		// A superset, not an exact match: an open Dependabot pull request is synthesised into a rule
		// result of its own, which belongs in the branch but was never in the registry.
		listed.Should().Contain(
			RuleRegistry.Rules.Select(rule => rule.RuleId),
			"a rule nothing fails is still a rule the estate is held to, and hiding it makes the "
			+ "branch a list of today's problems rather than what is governed");
	}

	[Fact]
	public void EveryCategoryThatOwnsARule_AppearsUnderIssues()
	{
		var listed = CategoryNodes(Tree(assessed: true)).Select(item => item.Category).ToList();

		listed.Should().Contain(
			RuleRegistry.Rules.Select(rule => rule.Category).Distinct().Cast<AssessmentCategory?>());
	}

	[Fact]
	public void ARuleNothingFails_IsGreenAndCountsNothing()
	{
		var clean = RuleNodes(Tree(assessed: true))
			.Should().ContainSingle(item => item.RuleId == "CI-01").Subject;

		clean.IconCss.Should().Contain("text-success");
		clean.IssueCount.Should().Be(0);
		clean.AffectedRepoCount.Should().Be(0);
		clean.HasErrors.Should().BeFalse();
		clean.HasWarnings.Should().BeFalse();
	}

	[Fact]
	public void ACategoryNothingFails_IsGreen()
	{
		var clean = CategoryNodes(Tree(assessed: true))
			.Should().ContainSingle(item => item.Category == AssessmentCategory.Testing).Subject;

		clean.IconCss.Should().Contain("text-success");
		clean.HasErrors.Should().BeFalse();
		clean.HasWarnings.Should().BeFalse();
	}

	[Fact]
	public void AFailingRule_KeepsItsSeverityColourAndCount()
	{
		var failing = RuleNodes(Tree(assessed: true))
			.Should().ContainSingle(item => item.RuleId == _failingRuleId).Subject;

		failing.IconCss.Should().Contain("text-danger");
		failing.IssueCount.Should().Be(1);
		failing.HasErrors.Should().BeTrue();
	}

	[Fact]
	public void AFailingRule_SortsAboveTheCleanRulesInItsCategory()
	{
		var inCategory = RuleNodes(Tree(assessed: true))
			.Where(item => item.Category == _failingCategory)
			.ToList();

		var failing = inCategory.Single(item => item.RuleId == _failingRuleId);
		var clean = inCategory.Where(item => item.RuleId != _failingRuleId).ToList();

		clean.Should().NotBeEmpty("the fixture's category owns more rules than the one that fails");
		clean.Should().OnlyContain(item => item.SortOrder > failing.SortOrder,
			"what needs attention belongs above what does not");
	}

	[Fact]
	public void AFailingCategory_IsNotGreen()
		=> CategoryNodes(Tree(assessed: true))
			.Should().ContainSingle(item => item.Category == _failingCategory)
			.Which.IconCss.Should().Contain("text-danger");

	[Fact]
	public void NothingAssessed_ListsNoRulesAtAll()
	{
		var tree = Tree(assessed: false);

		RuleNodes(tree).Should().BeEmpty(
			"painting the catalogue green over an organisation nobody has assessed would claim a "
			+ "clean bill of health that was never checked");
		CategoryNodes(tree).Should().BeEmpty();
	}

	[Fact]
	public void TheIssuesNode_IsNoLongerALeafOnceAssessed()
		=> Tree(assessed: true)
			.Should().ContainSingle(item => item.Key == NavTreeDataProvider.IssuesKey("panoramicdata"))
			.Which.IsLeaf.Should().BeFalse();
}
