using Microsoft.Extensions.Time.Testing;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Remediations.NuGetHygiene;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that a package freshness failure can actually be fixed: the advisory it raises carries what
/// the remediation reads, and the remediation refuses the job when it does not.
/// </summary>
/// <remarks>
/// The two halves drifted apart once already — the rule renamed its payload and the remediation kept
/// reading the old key, so the dashboard offered a fix that silently changed nothing. These tests
/// join them end to end so the next rename fails here instead of in front of a user.
/// </remarks>
public class NuGetPackageUpdateRemediationTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly DateTimeOffset _published = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task ShouldCarryTheStructuredUpdatesTheRemediationReads()
	{
		var result = await EvaluateBehindEstateAsync();

		result.Passed.Should().BeFalse();
		result.Advisory.Should().NotBeNull();
		result.Advisory!.Data.Should().ContainKey("updates");

		var updates = (string[])result.Advisory.Data["updates"];
		updates.Should().ContainSingle()
			.Which.Should().Be("Directory.Packages.props|Codacy.Api|PackageVersionAttribute|3.0.11|3.0.43");
	}

	[Fact]
	public async Task ShouldStillCarryTheRenderedFindingsForAHumanToRead()
	{
		var result = await EvaluateBehindEstateAsync();

		var behindEstate = (string[])result.Advisory!.Data["behind_estate"];
		behindEstate.Should().ContainSingle()
			.Which.Should().Contain("3.0.43", "the prose a human reads is not what the remediation parses");
	}

	[Fact]
	public async Task ShouldRewriteTheDeclaredVersionWhenTheRemediationRuns()
	{
		var result = await EvaluateBehindEstateAsync();

		var localPath = Path.Combine(Path.GetTempPath(), "nugetmanagement-tests", Guid.NewGuid().ToString("n"));
		Directory.CreateDirectory(localPath);
		var propsPath = Path.Combine(localPath, "Directory.Packages.props");
		await File.WriteAllTextAsync(propsPath, DeclaredProps("3.0.11"), TestContext.Current.CancellationToken);

		var applied = new List<string>();
		new NuGetBuildLevelUpdatesRemediation().Apply(localPath, result, applied, Output.WriteLine);

		applied.Should().ContainSingle().Which.Should().Be("Directory.Packages.props");

		var rewritten = await File.ReadAllTextAsync(propsPath, TestContext.Current.CancellationToken);
		rewritten.Should().Contain("Version=\"3.0.43\"");
	}

	[Fact]
	public async Task ShouldNotOfferAFixWhenTheAdvisoryCarriesNoUpdates()
	{
		// What the dashboard asks before it draws the wrench and counts the fix. An advisory naming a
		// remediation type it has not supplied the data for must answer no, or the user is promised a
		// fix that cannot run.
		var result = await EvaluateBehindEstateAsync();
		result.Advisory!.Data.Remove("updates");

		new NuGetBuildLevelUpdatesRemediation().CanRemediate(result).Should().BeFalse();
	}

	[Fact]
	public async Task ShouldOfferAFixWhenTheAdvisoryIsComplete()
	{
		var result = await EvaluateBehindEstateAsync();

		new NuGetBuildLevelUpdatesRemediation().CanRemediate(result).Should().BeTrue();
	}

	private static string DeclaredProps(string declaredVersion)
		=> $"""
			<Project>
			  <ItemGroup>
			    <PackageVersion Include="Codacy.Api" Version="{declaredVersion}" />
			  </ItemGroup>
			</Project>
			""";

	/// <summary>A PKG-05 failure raised by the estate floor, which needs no upstream knowledge.</summary>
	private static async Task<RuleResult> EvaluateBehindEstateAsync()
	{
		var rule = new NuGetBuildLevelUpdatesRule(
			new NuGetVersionCache(null),
			FrozenFloor("Codacy.Api", "3.0.43"),
			new FakeTimeProvider(_published.AddDays(1)));

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
				["Directory.Packages.props"] = DeclaredProps("3.0.11")
			}
		};

		return await rule.EvaluateAsync(context, TestContext.Current.CancellationToken).ConfigureAwait(false);
	}

	private static NuGetFloorCatalog FrozenFloor(string packageId, string version)
	{
		var path = Path.Combine(Path.GetTempPath(), "nugetmanagement-tests", Guid.NewGuid().ToString("n"));
		Directory.CreateDirectory(path);
		var file = Path.Combine(path, NuGetFloorCatalog.FileName);

		new NuGetFloorCatalog(file).Observe(packageId, version);
		return new NuGetFloorCatalog(file);
	}
}
