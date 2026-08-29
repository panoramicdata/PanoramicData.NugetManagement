using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that a repository excluded from governance stops colouring the branches above it.
/// </summary>
/// <remarks>
/// Excluding is the answer to a repository we cannot or will not act on, so leaving it in the roll-up
/// defeats the point: an unassessed one is Unknown, Unknown outranks every real severity, and one of
/// them greys the whole spine from Repositories to Organisations. The node itself stays in the tree,
/// dimmed — excluded means "does not count", not "hidden", or there would be no way to bring it back.
/// </remarks>
public class ExcludedRepositoryRollupTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _temporaryDirectory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	private const string _governed = "panoramicdata/Governed.Api";
	private const string _excluded = "panoramicdata/Excluded.Cli";

	[Fact]
	public void TheRepositoriesBranchShouldIgnoreAnExcludedRepository()
		=> NodeStatus(BuildTree(), NavTreeDataProvider.ReposKey("panoramicdata"))
			.Should().Be(PackageHealthStatus.Error);

	[Fact]
	public void TheOrganisationShouldIgnoreAnExcludedRepository()
		=> NodeStatus(BuildTree(), NavTreeDataProvider.OrgKey("panoramicdata"))
			.Should().Be(PackageHealthStatus.Error);

	[Fact]
	public void TheEstateShouldIgnoreAnExcludedRepository()
		=> NodeStatus(BuildTree(), NavTreeDataProvider.OrganisationsKey)
			.Should().Be(PackageHealthStatus.Error);

	[Fact]
	public void AnExcludedRepositoryShouldStillAppearInTheTree()
		=> BuildTree()
			.Where(item => item.ParentKey == NavTreeDataProvider.ReposKey("panoramicdata"))
			.Select(item => item.Text)
			.Should().Contain("Excluded.Cli", "excluding hides it from the totals, not from the tree");

	[Fact]
	public void AnExcludedRepositoryShouldNotContributeItsIssueCount()
		=> BuildTree().Single(item => item.Key == NavTreeDataProvider.OrgKey("panoramicdata"))
			.IssueCount.Should().Be(1, "only the governed repository's single failure counts");

	[Fact]
	public void TheBranchShouldStillBeGreyWhenTheUnassessedRepositoryIsGoverned()
		=> NodeStatus(BuildTree(excludeTheUnassessedOne: false), NavTreeDataProvider.ReposKey("panoramicdata"))
			.Should().Be(PackageHealthStatus.Unknown, "an unassessed repository we do govern is still unknown");

	private static PackageHealthStatus? NodeStatus(List<NavItem> items, string key)
		=> items.Single(item => item.Key == key).HealthStatus;

	private List<NavItem> BuildTree(bool excludeTheUnassessedOne = true)
	{
		var rows = new List<RepositoryDashboardRow>
		{
			// Assessed, and failing with an Error: this is the colour the branches should take.
			new()
			{
				Organization = "panoramicdata",
				RepositoryFullName = _governed,
				Packages = [new() { PackageId = "Governed.Api" }],
				Assessment = new RepoAssessment
				{
					RepositoryFullName = _governed,
					DefaultBranch = "main",
					AssessedAtUtc = DateTimeOffset.UtcNow,
					RuleResults =
					[
						new()
						{
							RuleId = "CQ-03",
							RuleName = "Codacy configured",
							Category = AssessmentCategory.CodeQuality,
							Severity = AssessmentSeverity.Error,
							Passed = false,
							Message = "Something is wrong."
						}
					]
				}
			},

			// Never assessed, so Unknown — the status that outranks Error and greys everything above.
			new()
			{
				Organization = "panoramicdata",
				RepositoryFullName = _excluded,
				Packages = [new() { PackageId = "Excluded.Cli" }]
			}
		};

		Directory.CreateDirectory(_temporaryDirectory);
		var cache = new DashboardCacheService(
			NullLogger<DashboardCacheService>.Instance,
			Path.Combine(_temporaryDirectory, "dashboard-cache.json"));
		cache.SetRows(rows);

		var settings = Options.Create(new AppSettings { NuGetOrganization = "panoramicdata" });

		// A settings file of this test's own: the two-argument constructor reads and writes the
		// developer's real runtime-settings.json.
		var runtimeSettings = new RuntimeSettingsService(
			settings,
			NullLogger<RuntimeSettingsService>.Instance,
			Path.Combine(_temporaryDirectory, "runtime-settings.json"));

		if (excludeTheUnassessedOne)
		{
			runtimeSettings.SetRepositoryExcluded(_excluded, true);
		}

		return new NavTreeDataProvider(cache, runtimeSettings, settings).BuildNavItems();
	}

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_temporaryDirectory))
			{
				Directory.Delete(_temporaryDirectory, recursive: true);
			}
		}
		catch (IOException)
		{
			// A leftover temp directory is not worth failing a test over.
		}

		GC.SuppressFinalize(this);
	}
}
