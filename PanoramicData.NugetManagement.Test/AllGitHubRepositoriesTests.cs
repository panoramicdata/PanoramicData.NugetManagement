using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that a GitHub repository of a governed organisation is shown when its project files declare a
/// package id — even before it publishes anything — and not otherwise, with archived ones and forks
/// marked excluded.
/// </summary>
public class AllGitHubRepositoriesTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private static readonly string[] _organizations = ["panoramicdata"];

	private readonly string _temporaryDirectory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	[Fact]
	public void ARepositoryDeclaringAPackageIdButPublishingNothingShouldGetARow()
	{
		var (rows, ungoverned) = Build([], Listed(Repo("panoramicdata/Salt.Api", declared: ["Salt.Api"])));

		var row = rows.Should().ContainSingle().Which;
		row.RepositoryFullName.Should().Be("panoramicdata/Salt.Api");
		row.Packages.Should().BeEmpty();
		row.DeclaredPackageIds.Should().Equal("Salt.Api");
		row.IsUnpackaged.Should().BeTrue();
		row.Organization.Should().Be("panoramicdata");
		row.IsGoverned.Should().BeTrue();
		ungoverned.Should().BeEmpty("a GitHub-derived row is not an unreadable package");
	}

	[Fact]
	public void ARepositoryDeclaringNoPackageIdShouldGetNoRow()
	{
		var (rows, _) = Build([], Listed(Repo("panoramicdata/Scripts", declared: [])));

		rows.Should().BeEmpty("a repository that declares no package is not a NuGet package repository");
	}

	[Fact]
	public void ARepositoryWhoseProjectFilesCouldNotBeReadShouldKeepItsPreviousRow()
	{
		var previous = new List<RepositoryDashboardRow>
		{
			new() { RepositoryFullName = "panoramicdata/Salt.Api", Organization = "panoramicdata", DeclaredPackageIds = ["Salt.Api"] }
		};

		var (rows, _) = DashboardService.BuildRows([], previous, _organizations, Listed(Repo("panoramicdata/Salt.Api", declared: null)));

		rows.Should().ContainSingle().Which.DeclaredPackageIds.Should().Equal("Salt.Api");
	}

	[Fact]
	public void ARowLeftByTheAllRepositoriesTreeShouldBeDropped()
	{
		var previous = new List<RepositoryDashboardRow>
		{
			new() { RepositoryFullName = "panoramicdata/Scripts", Organization = "panoramicdata" }
		};

		DashboardService.BuildRows([], previous, _organizations, Listed(Repo("panoramicdata/Scripts", declared: null)))
			.Rows.Should().BeEmpty("it never declared a package id");

		var failed = new Dictionary<string, IReadOnlyList<GitHubRepositoryInfo>?> { ["panoramicdata"] = null };
		DashboardService.BuildRows([], previous, _organizations, failed)
			.Rows.Should().BeEmpty("a failed listing carries forward only what declared a package id");
	}

	[Theory]
	[InlineData("<Project><PropertyGroup><PackageId>Salt.Api</PackageId></PropertyGroup></Project>", "Salt.Api")]
	[InlineData("<PackageId Condition=\"'$(Configuration)' == 'Release'\"> Meraki.Api </PackageId>", "Meraki.Api")]
	[InlineData("<packageid>$(AssemblyName)</packageid>", "$(AssemblyName)")]
	public void APackageIdElementShouldBeRead(string xml, string expected)
		=> DeclaredPackageIds.In(xml).Should().Equal(expected);

	[Theory]
	[InlineData("<Project><PropertyGroup><IsPackable>true</IsPackable></PropertyGroup></Project>")]
	[InlineData("<PackageId></PackageId>")]
	[InlineData("<PackageIdPrefix>Foo</PackageIdPrefix>")]
	public void NoPackageIdShouldBeReadWhereNoneIsDeclared(string xml)
		=> DeclaredPackageIds.In(xml).Should().BeEmpty();

	[Theory]
	[InlineData("Salt.Api/Salt.Api.csproj", true)]
	[InlineData("Directory.Build.props", true)]
	[InlineData("src/directory.build.props", true)]
	[InlineData("Salt.Api.slnx", false)]
	[InlineData("Directory.Packages.props", false)]
	public void OnlyProjectFilesAndDirectoryBuildPropsShouldBeRead(string path, bool candidate)
		=> DeclaredPackageIds.IsCandidate(path).Should().Be(candidate);

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
		var (rows, _) = Build(packages, Listed(Repo("PanoramicData/meraki.api"), Repo("panoramicdata/Salt.Api", declared: ["Salt.Api"])));

		rows.Should().HaveCount(2);
		rows.Single(r => r.RepositoryFullName.Equals("panoramicdata/Meraki.Api", StringComparison.OrdinalIgnoreCase))
			.Packages.Should().ContainSingle().Which.PackageId.Should().Be("Meraki.Api");
	}

	[Fact]
	public void AnArchivedRepositoryDeclaringAPackageIdShouldBeShownAndMarked()
	{
		var (rows, _) = Build([], Listed(Repo("panoramicdata/Old.Api", isArchived: true, declared: ["Old.Api"])));

		var row = rows.Should().ContainSingle().Which;
		row.IsArchived.Should().BeTrue();
		row.AutoExclusionReason.Should().Be("archived");
	}

	[Fact]
	public void AnUnpublishedArchivedRepositoryOrForkWhoseFilesWereNotReadShouldGetNoRow()
	{
		var (rows, _) = Build([], Listed(
			Repo("panoramicdata/Old.Thing", isArchived: true),
			Repo("panoramicdata/Upstream", isFork: true)));

		rows.Should().BeEmpty();
	}

	[Fact]
	public void APackagedForkShouldBeShownAndMarked()
	{
		var packages = new List<NuGetPackageInfo> { Package("Upstream", "https://github.com/panoramicdata/Upstream") };

		var (rows, _) = Build(packages, Listed(Repo("panoramicdata/Upstream", isFork: true)));

		rows.Should().ContainSingle().Which.AutoExclusionReason.Should().Be("fork");
	}

	[Fact]
	public void ARepositoryRenamedThenArchivedIsMarkedUnderTheNameItsPackageRecords()
	{
		// The package's nuspec still names the old repository; the organisation's list shows only the new
		// name. The listing step adds the answer GitHub gives for the old name, carried under that old name.
		var packages = new List<NuGetPackageInfo>
		{
			Package("DocMagic.Api", "https://github.com/panoramicdata/PanoramicData.DocMagic")
		};

		var (rows, _) = Build(
			packages,
			Listed(
				Repo("panoramicdata/PanoramicData.DocMagic.Archived", isArchived: true),
				Repo("panoramicdata/PanoramicData.DocMagic", isArchived: true)));

		var row = rows.Should().ContainSingle().Which;
		row.RepositoryFullName.Should().Be("panoramicdata/PanoramicData.DocMagic");
		row.IsArchived.Should().BeTrue("an archived repository is excluded from governance, however it was renamed");
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
		var (rows, _) = Build([], Listed(Repo("somebody-else/Thing", declared: ["Thing"])));

		rows.Should().BeEmpty();
	}

	[Fact]
	public void AFailedListingShouldKeepTheUnpublishedPackageRowsAlreadyShown()
	{
		var previous = new List<RepositoryDashboardRow>
		{
			new() { RepositoryFullName = "panoramicdata/Salt.Api", Organization = "panoramicdata", IsFork = true, DeclaredPackageIds = ["Salt.Api"] }
		};

		var failed = new Dictionary<string, IReadOnlyList<GitHubRepositoryInfo>?> { ["panoramicdata"] = null };
		var (rows, _) = DashboardService.BuildRows([], previous, _organizations, failed);

		var row = rows.Should().ContainSingle().Which;
		row.RepositoryFullName.Should().Be("panoramicdata/Salt.Api");
		row.IsFork.Should().BeTrue();
		row.DeclaredPackageIds.Should().Equal("Salt.Api");
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

	private static GitHubRepositoryInfo Repo(string fullName, bool isArchived = false, bool isFork = false, string[]? declared = null)
		=> new(fullName, $"https://github.com/{fullName}", isArchived, isFork, declared);

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
