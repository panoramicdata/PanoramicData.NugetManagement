using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="PublishedVersionRefresher"/>, which re-reads each package's published version
/// at assessment time.
/// </summary>
/// <remarks>
/// Without this, the only thing that ever wrote a published version was package discovery, and
/// Re-assess deliberately skips discovery. CI-11 therefore kept comparing a fresh tag against an
/// hours-old version and insisting a released package had never been published — pressing Re-assess
/// could not change its mind, because nothing in that path had asked NuGet anything.
/// </remarks>
public class PublishedVersionRefresherTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public async Task UpdatesTheVersion_WhenNuGetHasMovedOnSinceDiscovery()
	{
		var row = Row(("Athonet.Api", "1.0.47"));

		await Refresher(("Athonet.Api", "1.0.49")).RefreshAsync(row, TestContext.Current.CancellationToken);

		row.Packages[0].LatestVersion.Should().Be("1.0.49");
	}

	[Fact]
	public async Task KeepsTheCachedVersion_WhenTheLookupFails()
	{
		// A version we knew a minute ago beats no version at all: blanking it would turn a transient
		// nuget.org failure into "this package has never been published", which is CI-11's loudest
		// finding.
		var row = Row(("Athonet.Api", "1.0.47"));

		await new PublishedVersionRefresher(new ThrowingSource()).RefreshAsync(row, TestContext.Current.CancellationToken);

		row.Packages[0].LatestVersion.Should().Be("1.0.47");
	}

	[Fact]
	public async Task KeepsTheCachedVersion_WhenNuGetKnowsNoVersionOfThePackage()
	{
		var row = Row(("Athonet.Api", "1.0.47"));

		await Refresher(("Athonet.Api", null)).RefreshAsync(row, TestContext.Current.CancellationToken);

		row.Packages[0].LatestVersion.Should().Be("1.0.47");
	}

	[Fact]
	public async Task RecordsAFirstVersion_WhenNoneWasKnownBefore()
	{
		var row = Row(("Athonet.Api", null));

		await Refresher(("Athonet.Api", "1.0.49")).RefreshAsync(row, TestContext.Current.CancellationToken);

		row.Packages[0].LatestVersion.Should().Be("1.0.49");
	}

	[Fact]
	public async Task RefreshesEveryPackage_WhenARepositoryPublishesMoreThanOne()
	{
		var row = Row(("Acme.Core", "1.0.0"), ("Acme.Extensions", "1.0.0"));

		await Refresher(("Acme.Core", "2.0.0"), ("Acme.Extensions", "3.0.0"))
			.RefreshAsync(row, TestContext.Current.CancellationToken);

		row.Packages.Select(package => package.LatestVersion).Should().Equal("2.0.0", "3.0.0");
	}

	[Fact]
	public async Task AsksNuGetNothing_WhenTheRepositoryPublishesNoPackages()
	{
		// Every repository is assessed, most of them repeatedly. A repository that publishes nothing
		// has nothing to look up, and must not cost a request to discover that.
		var source = new FakeSource([]);
		var row = Row();

		await new PublishedVersionRefresher(source).RefreshAsync(row, TestContext.Current.CancellationToken);

		source.Requests.Should().BeEmpty();
	}

	private static PublishedVersionRefresher Refresher(params (string PackageId, string? Version)[] published)
		=> new(new FakeSource(published.ToDictionary(p => p.PackageId, p => p.Version, StringComparer.OrdinalIgnoreCase)));

	private static RepositoryDashboardRow Row(params (string PackageId, string? Version)[] packages)
		=> new()
		{
			RepositoryFullName = "panoramicdata/Athonet.Api",
			Packages = [.. packages.Select(p => new PublishedPackage
			{
				PackageId = p.PackageId,
				LatestVersion = p.Version
			})]
		};

	private sealed class FakeSource(Dictionary<string, string?> published) : IPublishedVersionSource
	{
		public List<string> Requests { get; } = [];

		public Task<string?> GetLatestPublishedVersionAsync(string packageId, CancellationToken cancellationToken)
		{
			Requests.Add(packageId);
			return Task.FromResult(published.GetValueOrDefault(packageId));
		}
	}

	private sealed class ThrowingSource : IPublishedVersionSource
	{
		public Task<string?> GetLatestPublishedVersionAsync(string packageId, CancellationToken cancellationToken)
			=> throw new HttpRequestException("nuget.org unreachable");
	}
}
