using AwesomeAssertions;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using Xunit;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// PKG-13: a repository still on FluentAssertions is one the estate has left behind, and bumping it
/// is the wrong fix.
/// </summary>
public class FluentAssertionsMigrationRuleTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RepositoryContext Ctx(params (string Path, string Content)[] files)
		=> new()
		{
			FullName = "panoramicdata/Example",
			Name = "Example",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = [.. files.Select(f => f.Path)],
			FileContents = files.ToDictionary(f => f.Path, f => f.Content, StringComparer.OrdinalIgnoreCase)
		};

	private static (string, string) Packages(params (string Id, string Version)[] packages)
		=> ("Directory.Packages.props",
			"<Project><ItemGroup>"
			+ string.Concat(packages.Select(p =>
				$"""<PackageVersion Include="{p.Id}" Version="{p.Version}" />"""))
			+ "</ItemGroup></Project>");

	private static Task<RuleResult> EvaluateAsync(RepositoryContext context)
		=> new FluentAssertionsMigrationRule().EvaluateAsync(context, CancellationToken.None);

	[Fact]
	public async Task ARepositoryUsingAwesomeAssertions_Passes()
	{
		var result = await EvaluateAsync(Ctx(Packages(("AwesomeAssertions", "9.6.0"))));

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task ARepositoryWithNoPackagesAtAll_Passes()
	{
		var result = await EvaluateAsync(Ctx());

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task ARepositoryStillOnFluentAssertions_Fails()
	{
		var result = await EvaluateAsync(Ctx(Packages(("FluentAssertions", "6.10.0"))));

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("FluentAssertions");
		result.Advisory!.Detail.Should().Contain("AwesomeAssertions");
	}

	[Fact]
	public async Task TheAnalyzersPackage_MovesToItsOwnFork()
	{
		var result = await EvaluateAsync(Ctx(Packages(("FluentAssertions.Analyzers", "0.34.1"))));

		result.Passed.Should().BeFalse();
		result.Advisory!.Detail.Should().Contain("AwesomeAssertions.Analyzers");
	}

	[Fact]
	public async Task TheFailure_CarriesNoRemediationType()
		=> (await EvaluateAsync(Ctx(Packages(("FluentAssertions", "6.10.0")))))
			.Advisory!.Data.Should().NotContainKey("remediation_type",
				"the package identity and the version have to change together, and a rename that kept "
				+ "6.10.0 would reference an AwesomeAssertions version that does not exist");

	[Fact]
	public void TheRule_DoesNotGovernFluentAssertionsAsADependency()
		=> new FluentAssertionsMigrationRule().Should().NotBeAssignableTo<IGovernsDependency>(
			"governing it would let triage report a FluentAssertions bump as covered by this, and "
			+ "this does not move that package's version — it removes the package");
}
