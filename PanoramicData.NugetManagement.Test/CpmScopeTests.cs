using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that CPM-02 follows the projects central package management actually governs. CPM is
/// resolved per directory, so a project under an opt-out gets its versions from its inline
/// attributes and nowhere else - issue 75, where removing them left the project unable to restore.
/// </summary>
public class CpmScopeTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _cpmEnabled =
		"<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>";

	private const string _cpmOptOut =
		"<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>";

	private const string _projectWithInlineVersion =
		"<Project><ItemGroup><PackageReference Include=\"Quartz\" Version=\"3.18.2\" /></ItemGroup></Project>";

	[Fact]
	public async Task CPM02_ShouldNotFlagAProjectThatOptsOutOfCentralPackageManagement()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = _cpmEnabled,
			["Worker.Jobs/Directory.Packages.props"] = _cpmOptOut,
			["Worker.Jobs/Worker.Jobs.csproj"] = _projectWithInlineVersion
		});

		var result = await GetRule("CPM-02").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue(
			"the inline version is the project's only source of a version, so removing it breaks restore");
	}

	[Fact]
	public async Task CPM02_ShouldStillFlagAProjectGovernedByCentralPackageManagement()
	{
		// Guards the exclusion above against over-reaching.
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = _cpmEnabled,
			["Lib/Lib.csproj"] = _projectWithInlineVersion
		});

		var result = await GetRule("CPM-02").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("Lib.csproj");
	}

	[Fact]
	public async Task CPM02_ShouldFlagOnlyTheGovernedProject_InAMixedRepository()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = _cpmEnabled,
			["Worker.Jobs/Directory.Packages.props"] = _cpmOptOut,
			["Worker.Jobs/Worker.Jobs.csproj"] = _projectWithInlineVersion,
			["Lib/Lib.csproj"] = _projectWithInlineVersion
		});

		var result = await GetRule("CPM-02").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("Lib.csproj");
		result.Message.Should().NotContain("Worker.Jobs.csproj");
	}

	[Fact]
	public async Task CPM02_ShouldLetTheNearestDirectoryPackagesPropsDecide()
	{
		// A nested opt-in under a repository-level opt-out is still governed: nearest wins, as MSBuild
		// resolves it.
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = _cpmOptOut,
			["Lib/Directory.Packages.props"] = _cpmEnabled,
			["Lib/Lib.csproj"] = _projectWithInlineVersion
		});

		var result = await GetRule("CPM-02").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse("the nearest Directory.Packages.props enables CPM");
		result.Message.Should().Contain("Lib.csproj");
	}

	private static IRule GetRule(string ruleId)
		=> RuleRegistry.Rules.First(r => r.RuleId == ruleId);

	private static RepositoryContext CreateContext(Dictionary<string, string> files) => new()
	{
		FullName = "test-org/Acme.Suite",
		Name = "Acme.Suite",
		DefaultBranch = "main",
		CurrentBranch = "main",
		Options = new RepoOptions(),
		FilePaths = [.. files.Keys],
		FileContents = files
	};
}
