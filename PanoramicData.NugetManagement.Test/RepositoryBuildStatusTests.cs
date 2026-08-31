using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests the per-repository build status: when it is remembered, when it stops being believed, and
/// the guarantee that it never colours the repository's health.
/// </summary>
public class RepositoryBuildStatusTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private const string _repo = "panoramicdata/Sample";

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
	/// A repository that passes every rule, so its health can only be green — and any colour the
	/// build state leaks into it would show up as a change from that.
	/// </summary>
	private List<NavItem> Tree(RepositoryBuildState? buildState)
	{
		var rows = new List<RepositoryDashboardRow>
		{
			new()
			{
				Organization = "panoramicdata",
				RepositoryFullName = _repo,
				Packages = [new() { PackageId = "Sample" }],
				IsClonedLocally = true,
				LastBuildState = buildState,
				LastBuiltAtUtc = buildState is null ? null : DateTimeOffset.UtcNow,
				Assessment = new RepoAssessment
				{
					RepositoryFullName = _repo,
					DefaultBranch = "main",
					AssessedAtUtc = DateTimeOffset.UtcNow,
					RuleResults =
					[
						new RuleResult
						{
							RuleId = "CI-01",
							RuleName = "CI workflow exists",
							Category = AssessmentCategory.CiCd,
							Severity = AssessmentSeverity.Error,
							Passed = true,
							Message = "found"
						}
					]
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

	private static NavItem RepositoryNode(List<NavItem> tree)
		=> tree.Single(item => item.Key == NavTreeDataProvider.RepoKey(_repo));

	private static NavItem OrganisationNode(List<NavItem> tree)
		=> tree.Single(item => item.Key == NavTreeDataProvider.OrgKey("panoramicdata"));

	[Fact]
	public void AFailedBuild_DoesNotChangeTheRepositoryIcon()
	{
		var withFailure = RepositoryNode(Tree(RepositoryBuildState.Failed)).IconCss;
		var withoutOne = RepositoryNode(Tree(null)).IconCss;

		withFailure.Should().Be(withoutOne,
			"the node's glyph answers how the repository scores against the rules, and a build "
			+ "failure is not a rule failure");
	}

	[Fact]
	public void AFailedBuild_DoesNotChangeTheOrganisationRollUp()
	{
		var withFailure = OrganisationNode(Tree(RepositoryBuildState.Failed));
		var withoutOne = OrganisationNode(Tree(null));

		withFailure.HealthStatus.Should().Be(withoutOne.HealthStatus);
		withFailure.HasErrors.Should().Be(withoutOne.HasErrors);
		withFailure.IssueCount.Should().Be(withoutOne.IssueCount);
	}

	[Theory]
	[InlineData(RepositoryBuildState.Failed)]
	[InlineData(RepositoryBuildState.Succeeded)]
	[InlineData(null)]
	public void TheRepositoryNode_CarriesTheBuildStateForRendering(RepositoryBuildState? state)
		=> RepositoryNode(Tree(state)).BuildState.Should().Be(state,
			"the badge is drawn from the node, so the node has to know");

	[Theory]
	[InlineData(WorkKind.FixAll)]
	[InlineData(WorkKind.FixCategory)]
	[InlineData(WorkKind.FixRule)]
	[InlineData(WorkKind.GitSync)]
	[InlineData(WorkKind.Clone)]
	public void WorkThatChangesTheWorkingTree_StopsTheBuildStatusBeingBelieved(WorkKind kind)
		=> BuildStatusLifetime.Invalidates(kind).Should().BeTrue(
			"a green badge has to mean that this exact tree built, or it is worse than no badge");

	[Theory]
	[InlineData(WorkKind.Build)]
	[InlineData(WorkKind.Test)]
	[InlineData(WorkKind.Reassess)]
	[InlineData(WorkKind.TriageDependabot)]
	[InlineData(WorkKind.Publish)]
	[InlineData(WorkKind.CommitAndPush)]
	public void WorkThatLeavesTheWorkingTreeAlone_KeepsTheBuildStatus(WorkKind kind)
		=> BuildStatusLifetime.Invalidates(kind).Should().BeFalse(
			"throwing the result away for work that changed no file would leave the estate grey for "
			+ "no reason");

	[Fact]
	public void EveryWorkKind_HasBeenConsidered()
		=> Enum.GetValues<WorkKind>().Should().OnlyContain(
			kind => BuildStatusLifetime.IsKnown(kind),
			"a work kind added without deciding whether it invalidates a build result would silently "
			+ "default to keeping it, which is the answer that can be wrong");
}
