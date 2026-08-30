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

	[Fact]
	public void OrgWorkNode_OnlyRepositoryLanesHaveItems_StillAppears()
	{
		// The spec requires the organisation's work node when its own lane has items OR any repository
		// beneath it does, and this is why: "Fix everything & push" runs a discovery item on the
		// organisation lane, fans it out into every repository's lane, and then completes and is
		// removed. With the narrower "own lane only" rule the node — and with it the "stop all" button
		// that is the compensation for a bulk action no longer being one thing to stop — would vanish
		// at the exact moment forty repository lanes filled up.
		var lanes = new WorkLaneService();
		lanes.Enqueue("Build", WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", Repo), "build", null, null);
		var provider = NewProvider(lanes, withRepository: Repo);

		var node = provider.BuildNavItems()
			.Should().ContainSingle(i => i.Key == NavTreeDataProvider.OrgWorkKey("panoramicdata")).Subject;

		node.ParentKey.Should().Be(NavTreeDataProvider.OrgKey("panoramicdata"));
		node.Text.Should().Be(
			"Work (1 below)",
			"the count must say where the work is — it is not in this lane, and expanding the node will not find it");
		node.IsLeaf.Should().BeTrue("the organisation lane has nothing of its own to list");
	}

	[Fact]
	public void OrgWorkNode_BothLanesHaveItems_CountsThemSeparately()
	{
		var lanes = new WorkLaneService();
		lanes.Enqueue("Rediscover", WorkDescriptor.ForOrganization(WorkKind.RediscoverOrganization, "panoramicdata"), "rd", null, null);
		lanes.Enqueue("Build", WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", Repo), "build", null, null);
		var provider = NewProvider(lanes, withRepository: Repo);

		provider.BuildNavItems()
			.Single(i => i.Key == NavTreeDataProvider.OrgWorkKey("panoramicdata"))
			.Text.Should().Be("Work (1 here, 1 below)");
	}

	[Fact]
	public void OrgWorkNode_NothingAnywhere_IsAbsent()
	{
		var provider = NewProvider(new WorkLaneService(), withRepository: Repo);

		provider.BuildNavItems()
			.Should().NotContain(i => i.Key == NavTreeDataProvider.OrgWorkKey("panoramicdata"));
	}

	[Fact]
	public void RepositoryNode_LaneIsRunning_IsMarkedBusy()
	{
		// There is deliberately no estate-wide roll-up node: the spec's stated justification is that
		// activity is found by the spinner on the repository node. That spinner is this flag.
		var lanes = new WorkLaneService();
		lanes.Enqueue("Build", WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", Repo), "build", null, null);
		lanes.TryStartNext(out _);
		var provider = NewProvider(lanes, withRepository: Repo);

		provider.BuildNavItems()
			.Single(i => i.Key == NavTreeDataProvider.RepoKey(Repo))
			.IsBusy.Should().BeTrue();
	}

	[Fact]
	public void RepositoryNode_OnlyPendingItems_IsNotBusy()
	{
		// A lane full of pending items is waiting, not working. Spinning for it would make every
		// repository in the estate spin for the duration of a bulk action, which says nothing.
		var lanes = new WorkLaneService();
		lanes.Enqueue("Build", WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", Repo), "build", null, null);
		var provider = NewProvider(lanes, withRepository: Repo);

		provider.BuildNavItems()
			.Single(i => i.Key == NavTreeDataProvider.RepoKey(Repo))
			.IsBusy.Should().BeFalse();
	}

	[Fact]
	public void RepositoryNode_NoLane_IsNotBusy()
	{
		var provider = NewProvider(new WorkLaneService(), withRepository: Repo);

		provider.BuildNavItems()
			.Single(i => i.Key == NavTreeDataProvider.RepoKey(Repo))
			.IsBusy.Should().BeFalse();
	}

	[Fact]
	public void WorkItemNodes_KeepTheLanesOrderNotAlphabeticalOrder()
	{
		var lanes = new WorkLaneService();

		// Titles deliberately alphabetise in the opposite order to the queue, so a regression that
		// dropped "SortOrder = index" — leaving every node at the default 0, tie-broken by Text —
		// would still slip past a test that only checked membership.
		lanes.Enqueue("Zebra build", WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", Repo), "z", null, null);
		lanes.Enqueue("Apple test", WorkDescriptor.ForRepository(WorkKind.Test, "panoramicdata", Repo), "a", null, null);
		lanes.Enqueue("Middle publish", WorkDescriptor.ForRepository(WorkKind.Publish, "panoramicdata", Repo), "m", null, null);
		var provider = NewProvider(lanes, withRepository: Repo);

		var nodes = provider.BuildNavItems()
			.Where(i => i.ParentKey == NavTreeDataProvider.WorkKey(Repo))
			.ToList();

		nodes.Select(n => n.Text).Should().ContainInOrder("Zebra build", "Apple test", "Middle publish");

		var zebra = nodes.Single(n => n.Text == "Zebra build");
		var apple = nodes.Single(n => n.Text == "Apple test");
		zebra.SortOrder.Should().BeLessThan(
			apple.SortOrder,
			"the queue's order is the information — alphabetising it would make the tree lie about what runs next");
	}

	[Fact]
	public void WorkNode_MixedCaseRepositoryName_StillFindsItsLane()
	{
		var lanes = new WorkLaneService();

		// Queued in a different casing to the repository row below. WorkDescriptor.LaneKey lower-cases
		// both forms, so this must still find the lane — the failure mode without that lower-casing is
		// silent: no work node, no error, nothing in the browser console to explain it.
		lanes.Enqueue(
			"Build",
			WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", "PanoramicData/ATHONET.API"),
			"build",
			null,
			null);
		var provider = NewProvider(lanes, withRepository: Repo);

		provider.BuildNavItems()
			.Should().ContainSingle(i => i.Key == NavTreeDataProvider.WorkKey(Repo))
			.Which.ParentKey.Should().Be(NavTreeDataProvider.RepoKey(Repo));
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
