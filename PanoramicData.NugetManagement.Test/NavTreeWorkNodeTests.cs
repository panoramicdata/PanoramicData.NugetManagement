using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that queued work appears in the navigation tree under the repository it belongs to. The
/// queue used to be a pane of its own below the tree, which was a second place to look for state
/// the tree already models per repository.
/// </summary>
public class NavTreeWorkNodeTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private const string Repo = "panoramicdata/Athonet.Api";

	private readonly string _cacheDirectory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	[Fact]
	public void WorkNode_LaneWithItems_AppearsUnderTheRepository()
	{
		var lanes = new WorkLaneService();
		lanes.Enqueue("Build", WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", Repo), "build", null, null);
		var provider = NewProvider(lanes, withRepository: Repo);

		var items = provider.BuildNavItems();

		items.Should().ContainSingle(i => i.Key == NavTreeDataProvider.WorkKey(Repo))
			.Which.ParentKey.Should().Be(NavTreeDataProvider.RepoKey(Repo));
	}

	[Fact]
	public void WorkNode_EmptyLane_IsAbsent()
	{
		var provider = NewProvider(new WorkLaneService(), withRepository: Repo);

		provider.BuildNavItems().Should().NotContain(i => i.Key == NavTreeDataProvider.WorkKey(Repo));
	}

	[Fact]
	public void WorkItemNodes_AreChildrenOfTheWorkNode()
	{
		var lanes = new WorkLaneService();
		var item = lanes.Enqueue("Build", WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", Repo), "build", null, null)!;
		var provider = NewProvider(lanes, withRepository: Repo);

		var node = provider.BuildNavItems()
			.Should().ContainSingle(i => i.WorkItemId == item.Id).Subject;

		node.ParentKey.Should().Be(NavTreeDataProvider.WorkKey(Repo));
		node.Text.Should().Be("Build");
		node.WorkItemState.Should().Be(WorkItemState.Pending);
		node.IsLeaf.Should().BeTrue();
	}

	[Fact]
	public void WorkItemNode_RunningWithProgress_CarriesBoth()
	{
		var lanes = new WorkLaneService();
		var item = lanes.Enqueue("Build", WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", Repo), "build", null, null)!;
		lanes.TryStartNext(out _);
		lanes.ReportProgress(item, "repo 8 of 47");
		var provider = NewProvider(lanes, withRepository: Repo);

		var node = provider.BuildNavItems().Single(i => i.WorkItemId == item.Id);

		node.WorkItemState.Should().Be(WorkItemState.Running);
		node.WorkItemProgress.Should().Be("repo 8 of 47");
	}

	[Fact]
	public void OrgWorkNode_OrganisationLaneWithItems_AppearsUnderTheOrganisation()
	{
		var lanes = new WorkLaneService();
		lanes.Enqueue("Rediscover", WorkDescriptor.ForOrganization(WorkKind.RediscoverOrganization, "panoramicdata"), "rd", null, null);
		var provider = NewProvider(lanes, withRepository: Repo);

		provider.BuildNavItems()
			.Should().ContainSingle(i => i.Key == NavTreeDataProvider.OrgWorkKey("panoramicdata"))
			.Which.ParentKey.Should().Be(NavTreeDataProvider.OrgKey("panoramicdata"));
	}

	/// <summary>
	/// Builds a provider over a cache primed with one repository, so each test needs only to describe
	/// the lane state it cares about.
	/// </summary>
	private NavTreeDataProvider NewProvider(WorkLaneService lanes, string withRepository)
	{
		var rows = new List<RepositoryDashboardRow>
		{
			new()
			{
				RepositoryFullName = withRepository,
				Organization = "panoramicdata",
				Packages = [new() { PackageId = "Athonet.Api", LatestVersion = "1.0.0" }]
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
			settings,
			workLanes: lanes);
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
