using Microsoft.Extensions.Time.Testing;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests the two questions the freshness rules now ask: are you behind the estate (immediate), and
/// have you been behind a published release for longer than its grace period.
/// </summary>
public class NuGetPackageUpdateGateTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly DateTimeOffset _published = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task ShouldFailWhenBehindTheEstateFloorEvenWithNoUpstreamKnowledge()
	{
		var result = await Evaluate(
			declaredVersion: "3.0.11",
			cache: new NuGetVersionCache(null),
			floors: FrozenFloor("Codacy.Api", "3.0.43"),
			now: _published.AddDays(1));

		result.Passed.Should().BeFalse("the estate has already proven 3.0.43 works");
		result.Message.Should().Contain("3.0.43");
	}

	[Fact]
	public async Task ShouldPassWhenBehindUpstreamButInsideTheGracePeriod()
	{
		var result = await Evaluate(
			declaredVersion: "3.0.42",
			cache: CacheWith("Codacy.Api", "3.0.43", _published),
			floors: new NuGetFloorCatalog(null),
			now: _published.AddDays(29));

		result.Passed.Should().BeTrue("30 days is the build-level grace");
	}

	[Fact]
	public async Task ShouldFailWhenBehindUpstreamForLongerThanTheGracePeriod()
	{
		var result = await Evaluate(
			declaredVersion: "3.0.42",
			cache: CacheWith("Codacy.Api", "3.0.43", _published),
			floors: new NuGetFloorCatalog(null),
			now: _published.AddDays(31));

		result.Passed.Should().BeFalse();
	}

	[Fact]
	public async Task ShouldPassWhenUpstreamIsUnknown()
	{
		var result = await Evaluate(
			declaredVersion: "3.0.42",
			cache: new NuGetVersionCache(null),
			floors: new NuGetFloorCatalog(null),
			now: _published.AddYears(5));

		result.Passed.Should().BeTrue("an empty cache means unknown, and unknown is never a failure");
	}

	[Fact]
	public async Task ShouldPassWhenAheadOfUpstream()
	{
		var result = await Evaluate(
			declaredVersion: "3.0.44",
			cache: CacheWith("Codacy.Api", "3.0.43", _published),
			floors: new NuGetFloorCatalog(null),
			now: _published.AddYears(5));

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task ShouldIgnoreAGapThatBelongsToAnotherRule()
	{
		// A major gap is PKG-07's to report, not PKG-05's.
		var result = await Evaluate(
			declaredVersion: "2.0.0",
			cache: CacheWith("Codacy.Api", "3.0.43", _published),
			floors: new NuGetFloorCatalog(null),
			now: _published.AddYears(5));

		result.Passed.Should().BeTrue("PKG-05 reports build-level gaps only");
	}

	private static NuGetVersionCache CacheWith(string packageId, string version, DateTimeOffset published)
	{
		var cache = new NuGetVersionCache(null);
		cache.Update(packageId, version, published, published);
		return cache;
	}

	/// <summary>A catalog whose frozen baseline already holds a floor, as it would on a second run.</summary>
	private static NuGetFloorCatalog FrozenFloor(string packageId, string version)
	{
		var path = Path.Combine(Path.GetTempPath(), "nugetmanagement-tests", Guid.NewGuid().ToString("n"));
		Directory.CreateDirectory(path);
		var file = Path.Combine(path, NuGetFloorCatalog.FileName);

		new NuGetFloorCatalog(file).Observe(packageId, version);
		return new NuGetFloorCatalog(file);
	}

	private static async Task<RuleResult> Evaluate(
		string declaredVersion,
		NuGetVersionCache cache,
		NuGetFloorCatalog floors,
		DateTimeOffset now)
	{
		var timeProvider = new FakeTimeProvider(now);
		var rule = new NuGetBuildLevelUpdatesRule(cache, floors, timeProvider);

		var context = new RepositoryContext
		{
			FullName = "panoramicdata/Sample.Api",
			Name = "Sample.Api",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = ["Directory.Packages.props"],
			FileContents = new Dictionary<string, string>
			{
				["Directory.Packages.props"] = $"""
					<Project>
					  <ItemGroup>
					    <PackageVersion Include="Codacy.Api" Version="{declaredVersion}" />
					  </ItemGroup>
					</Project>
					"""
			}
		};

		return await rule.EvaluateAsync(context, TestContext.Current.CancellationToken).ConfigureAwait(false);
	}
}
