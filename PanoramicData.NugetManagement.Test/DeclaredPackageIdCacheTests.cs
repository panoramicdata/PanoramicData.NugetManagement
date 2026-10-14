using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that the scan of unpublished repositories for a declared package id is only repeated for a
/// repository somebody has pushed to.
/// </summary>
public class DeclaredPackageIdCacheTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private static readonly DateTimeOffset _pushedAt = new(2026, 10, 12, 9, 30, 0, TimeSpan.Zero);

	private readonly string _directory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	private string CachePath => Path.Combine(_directory, "declared-package-ids.json");

	[Fact]
	public void AnAnswerShouldBeReusedWhileNobodyHasPushed()
	{
		var cache = new DeclaredPackageIdCache(null);
		cache.Set("panoramicdata/Salt.Api", _pushedAt, ["Salt.Api"]);

		cache.TryGet("panoramicdata/Salt.Api", _pushedAt, out var ids).Should().BeTrue();
		ids.Should().Equal("Salt.Api");
	}

	[Fact]
	public void AnAnswerShouldNotBeReusedOnceTheRepositoryHasBeenPushedTo()
	{
		var cache = new DeclaredPackageIdCache(null);
		cache.Set("panoramicdata/Salt.Api", _pushedAt, ["Salt.Api"]);

		cache.TryGet("panoramicdata/Salt.Api", _pushedAt.AddMinutes(1), out _).Should().BeFalse();
	}

	[Fact]
	public void DeclaringNothingShouldBeRememberedAsFirmlyAsDeclaringSomething()
	{
		var cache = new DeclaredPackageIdCache(null);
		cache.Set("panoramicdata/Scripts", _pushedAt, []);

		cache.TryGet("panoramicdata/Scripts", _pushedAt, out var ids).Should().BeTrue("re-reading the majority is where the calls went");
		ids.Should().BeEmpty();
	}

	[Fact]
	public void ARepositoryNeverReadShouldMiss()
		=> new DeclaredPackageIdCache(null).TryGet("panoramicdata/Salt.Api", _pushedAt, out _).Should().BeFalse();

	[Fact]
	public void TheRepositoryNameShouldNotBeCaseSensitive()
	{
		var cache = new DeclaredPackageIdCache(null);
		cache.Set("PanoramicData/salt.api", _pushedAt, ["Salt.Api"]);

		cache.TryGet("panoramicdata/Salt.Api", _pushedAt, out _).Should().BeTrue();
	}

	[Fact]
	public void TheSameInstantInADifferentOffsetShouldStillMatch()
	{
		var cache = new DeclaredPackageIdCache(null);
		cache.Set("panoramicdata/Salt.Api", _pushedAt, ["Salt.Api"]);

		cache.TryGet("panoramicdata/Salt.Api", _pushedAt.ToOffset(TimeSpan.FromHours(1)), out _).Should().BeTrue();
	}

	[Fact]
	public void AnswersShouldSurviveARestart()
	{
		var first = new DeclaredPackageIdCache(CachePath);
		first.Set("panoramicdata/Salt.Api", _pushedAt, ["Salt.Api"]);
		first.Set("panoramicdata/Scripts", _pushedAt, []);
		first.Save();

		var second = new DeclaredPackageIdCache(CachePath);

		second.TryGet("panoramicdata/Salt.Api", _pushedAt, out var ids).Should().BeTrue();
		ids.Should().Equal("Salt.Api");
		second.TryGet("panoramicdata/Scripts", _pushedAt, out var none).Should().BeTrue();
		none.Should().BeEmpty();
	}

	[Fact]
	public void NothingShouldBeWrittenUntilSomethingChanges()
	{
		var cache = new DeclaredPackageIdCache(CachePath);

		cache.Save();

		File.Exists(CachePath).Should().BeFalse();
	}

	[Fact]
	public void AnUnreadableFileShouldMeanAnEmptyCacheNotAFailure()
	{
		Directory.CreateDirectory(_directory);
		File.WriteAllText(CachePath, "{ this is not json");

		var cache = new DeclaredPackageIdCache(CachePath);

		cache.TryGet("panoramicdata/Salt.Api", _pushedAt, out _).Should().BeFalse();
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
