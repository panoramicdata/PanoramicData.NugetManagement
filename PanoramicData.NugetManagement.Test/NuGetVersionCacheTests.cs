using System.Text.Json;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests the committed snapshot of what nuget.org last reported. A missing or corrupt file must
/// leave every package "unknown" rather than invent a version: a guessed answer here becomes a
/// governance verdict against a repository.
/// </summary>
public class NuGetVersionCacheTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _directory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	[Fact]
	public void ShouldReadASnapshotThatWasWrittenToDisk()
	{
		WriteFile("""
			{
			  "Codacy.Api": {
			    "latestVersion": "3.0.43",
			    "published": "2026-08-12T00:00:00+00:00",
			    "refreshedAtUtc": "2026-08-29T00:00:00+00:00"
			  }
			}
			""");

		new NuGetVersionCache(Path.Combine(_directory, NuGetVersionCache.FileName))
			.TryGet("Codacy.Api", out var snapshot).Should().BeTrue();

		snapshot.LatestVersion.Should().Be("3.0.43");
		snapshot.Published.Should().Be(new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero));
	}

	[Fact]
	public void ShouldNotBeCaseSensitiveAboutPackageIds()
	{
		WriteFile("""
			{ "Codacy.Api": { "latestVersion": "3.0.43", "published": "2026-08-12T00:00:00+00:00", "refreshedAtUtc": "2026-08-29T00:00:00+00:00" } }
			""");

		new NuGetVersionCache(Path.Combine(_directory, NuGetVersionCache.FileName))
			.TryGet("codacy.api", out _).Should().BeTrue();
	}

	[Fact]
	public void AnAbsentFileShouldLeaveEveryPackageUnknown()
		=> new NuGetVersionCache(Path.Combine(_directory, NuGetVersionCache.FileName))
			.TryGet("Codacy.Api", out _).Should().BeFalse();

	[Fact]
	public void ACorruptFileShouldLeaveEveryPackageUnknownRatherThanThrow()
	{
		WriteFile("{ this is not json");

		new NuGetVersionCache(Path.Combine(_directory, NuGetVersionCache.FileName))
			.TryGet("Codacy.Api", out _).Should().BeFalse();
	}

	[Fact]
	public void ANullPathShouldBeAnEmptyInMemoryCache()
		=> new NuGetVersionCache(null).TryGet("Codacy.Api", out _).Should().BeFalse();

	[Fact]
	public void ANewVersionShouldBeReportedAsAChangeAndSurviveARestart()
	{
		var path = Path.Combine(_directory, NuGetVersionCache.FileName);
		Directory.CreateDirectory(_directory);

		var cache = new NuGetVersionCache(path);
		cache.Update("Codacy.Api", "3.0.43", Published, Now).Should().BeTrue();
		cache.Persist();

		new NuGetVersionCache(path).TryGet("Codacy.Api", out var reloaded).Should().BeTrue();
		reloaded.LatestVersion.Should().Be("3.0.43");
	}

	[Fact]
	public void RefreshingAnUnchangedVersionShouldNotCountAsAChange()
	{
		var cache = new NuGetVersionCache(null);
		cache.Update("Codacy.Api", "3.0.43", Published, Now).Should().BeTrue();

		cache.Update("Codacy.Api", "3.0.43", Published, Now.AddDays(1))
			.Should().BeFalse("the cache is committed, so a timestamp alone must not dirty the file");
	}

	[Fact]
	public void RefreshingAnUnchangedVersionShouldNotMoveItsRefreshedAt()
	{
		var cache = new NuGetVersionCache(null);
		cache.Update("Codacy.Api", "3.0.43", Published, Now);
		cache.Update("Codacy.Api", "3.0.43", Published, Now.AddDays(1));

		cache.TryGet("Codacy.Api", out var snapshot);
		snapshot.RefreshedAtUtc.Should().Be(Now, "refreshedAtUtc records what changed, not when we looked");
	}

	private static readonly DateTimeOffset Published = new(2026, 8, 12, 0, 0, 0, TimeSpan.Zero);
	private static readonly DateTimeOffset Now = new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);

	private void WriteFile(string json)
	{
		Directory.CreateDirectory(_directory);
		File.WriteAllText(Path.Combine(_directory, NuGetVersionCache.FileName), json);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_directory))
			{
				Directory.Delete(_directory, recursive: true);
			}
		}
		catch (IOException)
		{
			// A leftover temp directory is not worth failing a test over.
		}

		GC.SuppressFinalize(this);
	}
}
