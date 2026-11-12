using Microsoft.Extensions.Time.Testing;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// A package held in the repository config is left alone by the update rules, with the reason shown,
/// so a version another dependency's constraint forbids moving past does not keep the repository red.
/// </summary>
public class HeldPackagesTests
{
	private const string _package = "Microsoft.EntityFrameworkCore";
	private static readonly DateTimeOffset _published = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task AHeldPackageBehindANewMajorShouldPass_AndSayWhy()
	{
		var result = await Evaluate(held: new() { [_package] = "Npgsql provider supports EF Core 10 only" });

		result.Passed.Should().BeTrue();
		result.Message.Should().Contain("Held back deliberately").And.Contain("Npgsql provider supports EF Core 10 only");
	}

	[Fact]
	public async Task TheSamePackageNotHeldShouldFail()
		=> (await Evaluate(held: null)).Passed.Should().BeFalse();

	[Fact]
	public async Task AHoldShouldMatchThePackageIdIgnoringCase()
		=> (await Evaluate(held: new() { [_package.ToUpperInvariant()] = "reason" })).Passed.Should().BeTrue();

	[Fact]
	public async Task AHoldShouldNotExemptAnotherPackage()
		=> (await Evaluate(held: new() { ["Some.Other.Package"] = "reason" })).Passed.Should().BeFalse();

	[Fact]
	public void TheHoldShouldBeReadFromTheRepositoryConfigFile()
	{
		var config = NugetManagementRepositoryConfigParser.Parse("""{"heldPackages": {"Microsoft.EntityFrameworkCore": "Npgsql"}}""");

		config!.HeldReason("microsoft.entityframeworkcore").Should().Be("Npgsql");
		config.HeldReason("Other").Should().BeNull();
	}

	private static async Task<RuleResult> Evaluate(Dictionary<string, string>? held)
	{
		var cache = new NuGetVersionCache(null);
		cache.Update(_package, "11.0.0", _published, _published);

		var rule = new NuGetMajorLevelUpdatesRule(
			cache,
			new NuGetFloorCatalog(null),
			new FakeTimeProvider(_published.AddDays(5)));

		var context = new RepositoryContext
		{
			FullName = "panoramicdata/Sample",
			Name = "Sample",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			RepositoryConfig = held is null ? null : new NugetManagementRepositoryConfig { HeldPackages = held },
			FilePaths = ["Directory.Packages.props"],
			FileContents = new Dictionary<string, string>
			{
				["Directory.Packages.props"] = $"""<Project><ItemGroup><PackageVersion Include="{_package}" Version="10.0.14" /></ItemGroup></Project>"""
			}
		};

		return await rule.EvaluateAsync(context, TestContext.Current.CancellationToken).ConfigureAwait(false);
	}
}
