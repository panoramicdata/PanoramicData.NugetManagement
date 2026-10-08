using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that every GitHub repository of a governed organisation is shown — including those that
/// publish no package — with archived ones and forks visible but excluded, and unpackaged ones
/// assess-only.
/// </summary>
public class AllGitHubRepositoriesTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private static readonly string[] _organizations = ["panoramicdata"];

	private readonly string _temporaryDirectory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	[Fact]
	public void ARepositoryPublishingNothingShouldGetARow()
	{
		var (rows, ungoverned) = Build([], Listed(Repo("panoramicdata/Scripts")));

		var row = rows.Should().ContainSingle().Which;
		row.RepositoryFullName.Should().Be("panoramicdata/Scripts");
		row.Packages.Should().BeEmpty();
		row.IsUnpackaged.Should().BeTrue();
		row.Organization.Should().Be("panoramicdata");
		row.IsGoverned.Should().BeTrue();
		ungoverned.Should().BeEmpty("a GitHub-derived row is not an unreadable package");
	}

	[Theory]
	[InlineData(0, 0, false)]
	[InlineData(1, 0, true)]
	[InlineData(2, 1, false)]
	[InlineData(2, 2, false)]
	public void OnlyARepositoryThatRetiredEveryPackageShouldLeaveOnReassessment(int published, int listed, bool leaves)
		=> WorkExecutors.HasRetiredEveryPackage(published, listed).Should().Be(
			leaves,
			"a repository publishing nothing has retired nothing, so re-assessment must not remove it");

	[Fact]
	public void APackageDerivedRowShouldKeepItsPackagesAndAppearOnce()
	{
		var packages = new List<NuGetPackageInfo> { Package("Meraki.Api", "https://github.com/panoramicdata/Meraki.Api") };

		// Same repository, different case, as GitHub's canonical spelling can differ from a nuspec's.
		var (rows, _) = Build(packages, Listed(Repo("PanoramicData/meraki.api"), Repo("panoramicdata/Scripts")));

		rows.Should().HaveCount(2);
		rows.Single(r => r.RepositoryFullName.Equals("panoramicdata/Meraki.Api", StringComparison.OrdinalIgnoreCase))
			.Packages.Should().ContainSingle().Which.PackageId.Should().Be("Meraki.Api");
	}

	[Fact]
	public void AnArchivedRepositoryShouldBeShownAndMarked()
	{
		var (rows, _) = Build([], Listed(Repo("panoramicdata/Old.Api", isArchived: true)));

		var row = rows.Should().ContainSingle().Which;
		row.IsArchived.Should().BeTrue();
		row.AutoExclusionReason.Should().Be("archived");
	}

	[Fact]
	public void AForkShouldBeShownAndMarked()
	{
		var (rows, _) = Build([], Listed(Repo("panoramicdata/Upstream", isFork: true)));

		rows.Should().ContainSingle().Which.AutoExclusionReason.Should().Be("fork");
	}

	[Fact]
	public void APackagedRepositoryThatIsArchivedShouldBeMarkedToo()
	{
		var packages = new List<NuGetPackageInfo> { Package("Old.Api", "https://github.com/panoramicdata/Old.Api") };

		var (rows, _) = Build(packages, Listed(Repo("panoramicdata/Old.Api", isArchived: true)));

		var row = rows.Should().ContainSingle().Which;
		row.Packages.Should().ContainSingle();
		row.IsArchived.Should().BeTrue();
	}

	[Fact]
	public void ARepositoryOfAnotherOrganisationShouldNotBecomeARow()
	{
		var (rows, _) = Build([], Listed(Repo("somebody-else/Thing")));

		rows.Should().BeEmpty();
	}

	[Fact]
	public void AFailedListingShouldKeepTheUnpackagedRowsAlreadyShown()
	{
		var previous = new List<RepositoryDashboardRow>
		{
			new() { RepositoryFullName = "panoramicdata/Scripts", Organization = "panoramicdata", IsFork = true }
		};

		var failed = new Dictionary<string, IReadOnlyList<GitHubRepositoryInfo>?> { ["panoramicdata"] = null };
		var (rows, _) = DashboardService.BuildRows([], previous, _organizations, failed);

		var row = rows.Should().ContainSingle().Which;
		row.RepositoryFullName.Should().Be("panoramicdata/Scripts");
		row.IsFork.Should().BeTrue();
	}

	[Fact]
	public void WithoutGitHubTheBehaviourShouldBeExactlyAsBefore()
	{
		var previous = new List<RepositoryDashboardRow>
		{
			new() { RepositoryFullName = "panoramicdata/Scripts", Organization = "panoramicdata" }
		};

		var (rows, _) = DashboardService.BuildRows([], previous, _organizations);

		rows.Should().BeEmpty();
	}

	[Fact]
	public async Task AListingThatThrowsShouldBeReportedAsUnavailableRatherThanPropagating()
	{
		var result = await DashboardService.ListGitHubRepositoriesAsync(
			_organizations,
			(_, _) => throw new InvalidOperationException("rate limited"),
			NullLogger.Instance,
			CancellationToken.None);

		result.Should().ContainKey("panoramicdata").WhoseValue.Should().BeNull();
	}

	[Fact]
	public async Task NoClientShouldBeReportedAsUnavailable()
	{
		var result = await DashboardService.ListGitHubRepositoriesAsync(
			_organizations, null, NullLogger.Instance, CancellationToken.None);

		result.Should().ContainKey("panoramicdata").WhoseValue.Should().BeNull();
	}

	[Fact]
	public void UnpackagedRepositoriesShouldBeAssessOnly()
	{
		var row = new RepositoryDashboardRow { RepositoryFullName = "panoramicdata/Scripts" };

		foreach (var step in new[] { WorkflowStep.Fix, WorkflowStep.FixWithAi, WorkflowStep.Build, WorkflowStep.CommitAndPush, WorkflowStep.Publish })
		{
			RepositoryActionGate.Allows(row, step).Should().BeFalse($"{step} must not reach an unpackaged repository");
		}

		foreach (var step in new[] { WorkflowStep.GitSync, WorkflowStep.Reassess, WorkflowStep.Test })
		{
			RepositoryActionGate.Allows(row, step).Should().BeTrue($"{step} is read-only enough to allow");
		}
	}

	[Fact]
	public void PackagedRepositoriesShouldBeUnrestricted()
	{
		var row = new RepositoryDashboardRow
		{
			RepositoryFullName = "panoramicdata/Meraki.Api",
			Packages = [new() { PackageId = "Meraki.Api" }]
		};

		foreach (var step in Enum.GetValues<WorkflowStep>())
		{
			RepositoryActionGate.Allows(row, step).Should().BeTrue();
		}

		foreach (var kind in Enum.GetValues<WorkKind>())
		{
			RepositoryActionGate.Allows(row, kind).Should().BeTrue();
		}
	}

	[Theory]
	[InlineData(WorkKind.FixAll)]
	[InlineData(WorkKind.FixRule)]
	[InlineData(WorkKind.FixWithAiRule)]
	[InlineData(WorkKind.TriageDependabot)]
	[InlineData(WorkKind.Build)]
	[InlineData(WorkKind.CommitAndPush)]
	[InlineData(WorkKind.Publish)]
	public void OutwardWorkShouldBeRefusedForAnUnpackagedRepository(WorkKind kind)
		=> RepositoryActionGate.Allows(new RepositoryDashboardRow { RepositoryFullName = "panoramicdata/Scripts" }, kind)
			.Should().BeFalse();

	[Theory]
	[InlineData(WorkKind.Clone)]
	[InlineData(WorkKind.Reassess)]
	[InlineData(WorkKind.GitSync)]
	[InlineData(WorkKind.Test)]
	public void AssessmentWorkShouldBeAllowedForAnUnpackagedRepository(WorkKind kind)
		=> RepositoryActionGate.Allows(new RepositoryDashboardRow { RepositoryFullName = "panoramicdata/Scripts" }, kind)
			.Should().BeTrue();

	[Fact]
	public void TheToolbarShouldNotTargetAnUnpackagedRepositoryForAnOutwardStep()
	{
		var unpackaged = new RepositoryDashboardRow
		{
			RepositoryFullName = "panoramicdata/Scripts",
			Organization = "panoramicdata",
			IsClonedLocally = true
		};

		var selection = new RepositorySelection();
		selection.Select(unpackaged.RepositoryFullName);

		ToolbarScope.Targets([unpackaged], WorkflowStep.CommitAndPush, _ => false, selection).Should().BeEmpty();
		ToolbarScope.Targets([unpackaged], WorkflowStep.Fix, _ => false, selection).Should().BeEmpty();
		ToolbarScope.Targets([unpackaged], WorkflowStep.Test, _ => false, selection).Should().ContainSingle();
	}

	[Fact]
	public void PublishShouldBeOffAnUnpackagedRepositoryEvenWhenEverythingElseIsReady()
	{
		var unpackaged = new RepositoryDashboardRow
		{
			RepositoryFullName = "panoramicdata/Scripts",
			IsClonedLocally = true,
			IsWorkingTreeClean = true,
			IsSyncedWithOrigin = true,
			LastBuildState = RepositoryBuildState.Succeeded,
			LastTestState = RepositoryTestState.Passed
		};

		PublishGate.IsEnabled(unpackaged, allowWithoutTests: true).Should().BeFalse();
	}

	[Fact]
	public void AnArchivedRepositoryShouldAppearInTheTreeExcludedAndAForkToo()
	{
		var rows = new List<RepositoryDashboardRow>
		{
			new() { RepositoryFullName = "panoramicdata/Old.Api", Organization = "panoramicdata", IsArchived = true },
			new() { RepositoryFullName = "panoramicdata/Upstream", Organization = "panoramicdata", IsFork = true },
			new() { RepositoryFullName = "panoramicdata/Scripts", Organization = "panoramicdata" }
		};

		var items = BuildTree(rows);

		var nodes = items
			.Where(item => item.ParentKey == NavTreeDataProvider.ReposKey("panoramicdata"))
			.ToDictionary(item => item.Text);

		nodes.Keys.Should().BeEquivalentTo("Old.Api", "Upstream", "Scripts");
		nodes["Old.Api"].IsExcluded.Should().BeTrue();
		nodes["Upstream"].IsExcluded.Should().BeTrue();
		nodes["Scripts"].IsExcluded.Should().BeFalse("an ordinary unpackaged repository is assessed");
	}

	[Fact]
	public void AutoExclusionShouldNotBePersistedAndShouldClearWhenNoLongerTrue()
	{
		var settings = CreateSettings();

		settings.SetAutoExcluded("panoramicdata/Old.Api", "archived");
		settings.IsRepositoryExcluded("panoramicdata/old.api").Should().BeTrue();
		settings.AutoExclusionReason("panoramicdata/Old.Api").Should().Be("archived");

		CreateSettings().IsRepositoryExcluded("panoramicdata/Old.Api").Should().BeFalse("it is derived, not saved");

		settings.SetAutoExcluded("panoramicdata/Old.Api", null);
		settings.IsRepositoryExcluded("panoramicdata/Old.Api").Should().BeFalse();
	}

	private static (List<RepositoryDashboardRow> Rows, List<UngovernedPackage> Ungoverned) Build(
		List<NuGetPackageInfo> packages,
		Dictionary<string, IReadOnlyList<GitHubRepositoryInfo>?> github)
		=> DashboardService.BuildRows(packages, [], _organizations, github);

	private static Dictionary<string, IReadOnlyList<GitHubRepositoryInfo>?> Listed(params GitHubRepositoryInfo[] repositories)
		=> new() { ["panoramicdata"] = repositories };

	private static GitHubRepositoryInfo Repo(string fullName, bool isArchived = false, bool isFork = false)
		=> new(fullName, $"https://github.com/{fullName}", isArchived, isFork);

	private static NuGetPackageInfo Package(string packageId, string repositoryUrl)
		=> new()
		{
			PackageId = packageId,
			LatestVersion = "1.0.0",
			Organization = "panoramicdata",
			RepositoryUrl = repositoryUrl,
			RepositoryOwner = GitHubRepositoryUrl.Owner(repositoryUrl),
			RepositoryName = GitHubRepositoryUrl.Name(repositoryUrl),
			ResolutionOutcome = RepositoryResolutionOutcome.Resolved
		};

	private RuntimeSettingsService CreateSettings()
	{
		Directory.CreateDirectory(_temporaryDirectory);

		return new RuntimeSettingsService(
			Options.Create(new AppSettings { NuGetOrganization = "panoramicdata" }),
			NullLogger<RuntimeSettingsService>.Instance,
			Path.Combine(_temporaryDirectory, "runtime-settings.json"));
	}

	private List<NavItem> BuildTree(List<RepositoryDashboardRow> rows)
	{
		var settings = CreateSettings();

		foreach (var row in rows)
		{
			settings.SetAutoExcluded(row.RepositoryFullName, row.AutoExclusionReason);
		}

		var cache = new DashboardCacheService(
			NullLogger<DashboardCacheService>.Instance,
			Path.Combine(_temporaryDirectory, "dashboard-cache.json"));
		cache.SetRows(rows);

		return new NavTreeDataProvider(
			cache,
			settings,
			Options.Create(new AppSettings { NuGetOrganization = "panoramicdata" })).BuildNavItems();
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
