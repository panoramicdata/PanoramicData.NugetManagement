using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for excluding a repository from governance. The decision persists, because the repositories
/// most in need of it are the ones we cannot commit to — a package can declare a repository belonging
/// to somebody else, and deleting the clone only makes it come back on the next discovery.
/// </summary>
public class RepositoryExclusionTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _settingsDirectory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	[Fact]
	public void ARepositoryShouldNotBeExcludedByDefault()
		=> CreateService().IsRepositoryExcluded("panoramicdata/Meraki.Api").Should().BeFalse();

	[Fact]
	public void ExcludingShouldBeRemembered()
	{
		var service = CreateService();

		service.SetRepositoryExcluded("datahint-eu/vizor-echarts", true);

		service.IsRepositoryExcluded("datahint-eu/vizor-echarts").Should().BeTrue();
		service.ExcludedRepositories.Should().Contain("datahint-eu/vizor-echarts");
	}

	[Fact]
	public void ExclusionShouldNotBeCaseSensitive()
	{
		var service = CreateService();
		service.SetRepositoryExcluded("datahint-eu/vizor-echarts", true);

		service.IsRepositoryExcluded("DataHint-EU/Vizor-ECharts").Should().BeTrue(
			"a repository is the same repository however it is capitalised");
	}

	[Fact]
	public void BringingARepositoryBackShouldRemoveTheExclusion()
	{
		var service = CreateService();
		service.SetRepositoryExcluded("acme/Widget", true);

		service.SetRepositoryExcluded("acme/Widget", false);

		service.IsRepositoryExcluded("acme/Widget").Should().BeFalse();
	}

	[Fact]
	public void ExcludingTwiceShouldNotDuplicate()
	{
		var service = CreateService();

		service.SetRepositoryExcluded("acme/Widget", true);
		service.SetRepositoryExcluded("acme/Widget", true);

		service.ExcludedRepositories.Should().ContainSingle();
	}

	[Fact]
	public void AnEmptyNameShouldBeIgnored()
	{
		var service = CreateService();

		service.SetRepositoryExcluded("   ", true);

		service.ExcludedRepositories.Should().BeEmpty();
		service.IsRepositoryExcluded(null).Should().BeFalse();
	}

	private RuntimeSettingsService CreateService()
	{
		// The service persists to LocalApplicationData; point that at a directory of this test's own so
		// the developer's real settings are neither read nor written.
		Directory.CreateDirectory(_settingsDirectory);
		Environment.SetEnvironmentVariable("LOCALAPPDATA", _settingsDirectory);

		return new RuntimeSettingsService(
			Options.Create(new AppSettings { NuGetOrganization = "panoramicdata" }),
			NullLogger<RuntimeSettingsService>.Instance);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_settingsDirectory))
			{
				Directory.Delete(_settingsDirectory, recursive: true);
			}
		}
		catch (IOException)
		{
			// A leftover temp directory is not worth failing a test over.
		}

		GC.SuppressFinalize(this);
	}
}
