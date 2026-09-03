using Microsoft.Extensions.Time.Testing;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that a package the estate publishes itself gets no grace period against nuget.org.
/// </summary>
/// <remarks>
/// The grace period exists so that a verdict is not handed to whoever published this morning. For a
/// release of ours that reasoning does not apply — we published it — and waiting the full grace is
/// how a Dependabot pull request bumping one of our own packages sits open for a month with nothing
/// queued to move it.
/// </remarks>
public class OwnPackageZeroGraceTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly DateTimeOffset _published = new(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);

	private const string _ours = "PanoramicData.SheetMagic";

	[Fact]
	public async Task ABuildLevelReleaseOfOursShouldFailTheDayItIsPublished()
	{
		var result = await Evaluate<NuGetBuildLevelUpdatesRule>(
			declared: "3.1.138",
			latest: "3.1.140",
			now: _published.AddDays(1),
			owned: Owning(_ours));

		result.Passed.Should().BeFalse("we published 3.1.140, so there is nothing to wait for");
		result.Message.Should().Contain("3.1.140");
	}

	[Fact]
	public async Task AMinorReleaseOfOursShouldFailTheDayItIsPublished()
	{
		var result = await Evaluate<NuGetMinorLevelUpdatesRule>(
			declared: "3.1.138",
			latest: "3.2.0",
			now: _published.AddDays(1),
			owned: Owning(_ours));

		result.Passed.Should().BeFalse();
	}

	[Fact]
	public async Task AMajorReleaseOfOursShouldFailTheDayItIsPublished()
	{
		var result = await Evaluate<NuGetMajorLevelUpdatesRule>(
			declared: "3.1.138",
			latest: "4.0.0",
			now: _published.AddDays(1),
			owned: Owning(_ours));

		result.Passed.Should().BeFalse("PKG-07's 365-day grace does not apply to a major of ours");
	}

	[Fact]
	public async Task SomebodyElsesBuildLevelReleaseShouldStillGetItsGracePeriod()
	{
		var result = await Evaluate<NuGetBuildLevelUpdatesRule>(
			declared: "3.1.138",
			latest: "3.1.140",
			now: _published.AddDays(1),
			owned: new NuGetOwnedPackageCatalog(null));

		result.Passed.Should().BeTrue("the 30-day grace is untouched for packages we do not publish");
	}

	[Fact]
	public async Task AReleaseOfOursShouldNotFailBeforeItIsPublished()
	{
		// Cache refresh timestamps and a repository's clock can disagree. A release dated in the
		// future has not sat un-adopted for any length of time, and must not read as overdue.
		var result = await Evaluate<NuGetBuildLevelUpdatesRule>(
			declared: "3.1.138",
			latest: "3.1.140",
			now: _published.AddDays(-1),
			owned: Owning(_ours));

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task AFailureForOneOfOursShouldCarryTheRemediationThatMovesIt()
	{
		var result = await Evaluate<NuGetBuildLevelUpdatesRule>(
			declared: "3.1.138",
			latest: "3.1.140",
			now: _published.AddDays(1),
			owned: Owning(_ours));

		result.Advisory.Should().NotBeNull();
		result.Advisory!.Data["remediation_type"].Should().Be("update_package_versions");
		result.Advisory.Data["updates"].Should().BeOfType<string[]>()
			.Which.Should().ContainSingle().Which
			.Should().Be($"Directory.Packages.props|{_ours}|PackageVersionAttribute|3.1.138|3.1.140");
	}

	[Fact]
	public async Task AFailureForOneOfOursShouldNameItAsGovernedSoTriageCanSeeTheFixComing()
	{
		// DependabotTriageService reads governed_packages to decide that a failing rule will move a
		// pull request's dependency. Without the package named there, the pull request stays idle.
		var result = await Evaluate<NuGetBuildLevelUpdatesRule>(
			declared: "3.1.138",
			latest: "3.1.140",
			now: _published.AddDays(1),
			owned: Owning(_ours));

		result.Advisory!.Data[NuGetPackageUpdateRuleBase.GovernedPackagesKey]
			.Should().BeOfType<string[]>().Which.Should().Contain(_ours);
	}

	[Fact]
	public async Task AReleaseOfOursInsideTheGraceShouldSayItIsOursRatherThanMerelyOverdue()
	{
		var result = await Evaluate<NuGetBuildLevelUpdatesRule>(
			declared: "3.1.138",
			latest: "3.1.140",
			now: _published.AddDays(1),
			owned: Owning(_ours));

		result.Message.Should().Contain("we publish it", "the reason for no grace has to be readable");
	}

	/// <summary>A catalog whose baseline already records the package, as it would after discovery.</summary>
	private static NuGetOwnedPackageCatalog Owning(string packageId)
	{
		var catalog = new NuGetOwnedPackageCatalog(null);
		catalog.Record([packageId]);
		return catalog;
	}

	private static async Task<RuleResult> Evaluate<TRule>(
		string declared,
		string latest,
		DateTimeOffset now,
		NuGetOwnedPackageCatalog owned)
		where TRule : NuGetPackageUpdateRuleBase
	{
		var cache = new NuGetVersionCache(null);
		cache.Update(_ours, latest, _published, _published);

		var rule = (TRule)Activator.CreateInstance(
			typeof(TRule),
			cache,
			new NuGetFloorCatalog(null),
			new FakeTimeProvider(now),
			owned)!;

		var context = new RepositoryContext
		{
			FullName = "panoramicdata/AutoTask.Api",
			Name = "AutoTask.Api",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = ["Directory.Packages.props"],
			FileContents = new Dictionary<string, string>
			{
				["Directory.Packages.props"] = $"""
					<Project>
					  <ItemGroup>
					    <PackageVersion Include="{_ours}" Version="{declared}" />
					  </ItemGroup>
					</Project>
					"""
			}
		};

		return await rule.EvaluateAsync(context, TestContext.Current.CancellationToken).ConfigureAwait(false);
	}
}
