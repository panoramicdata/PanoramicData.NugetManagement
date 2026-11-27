using Microsoft.Extensions.Logging.Abstractions;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Remediations.CodeQuality;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// CQ-10: the opt-in parity build that shows Codacy's SonarC# and Lizard findings at build time, and the
/// remediation that adds it.
/// </summary>
public class CodacyParityBuildTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		$"codacy-parity-{Guid.NewGuid():N}");

	[Fact]
	public async Task Cq10_ShouldBeNotApplicable_WhenThereIsNoCSharpProject()
	{
		var result = await Evaluate(Files()).ConfigureAwait(true);

		result.IsApplicable.Should().BeFalse();
	}

	[Fact]
	public async Task Cq10_ShouldOfferAllThreeFiles_WhenNoneExist()
	{
		var result = await Evaluate(Files("App/App.csproj")).ConfigureAwait(true);

		result.Passed.Should().BeFalse();
		result.Advisory!.Data["remediation_type"].Should().Be("create_files");
		result.Advisory.Data["paths"].Should().BeEquivalentTo(new[]
		{
			CodacyParityStandards.TargetsPath,
			CodacyParityStandards.GlobalConfigPath,
			CodacyParityStandards.CodeMetricsPath
		});
	}

	[Fact]
	public async Task Cq10_ShouldOfferOnlyWhatIsMissing()
	{
		var files = Files("App/App.csproj", CodacyParityStandards.TargetsPath, CodacyParityStandards.GlobalConfigPath);
		files[CodacyParityStandards.TargetsPath] = CodacyParityStandards.TargetsContent;

		var result = await Evaluate(files).ConfigureAwait(true);

		result.Passed.Should().BeFalse();
		result.Advisory!.Data["paths"].Should().BeEquivalentTo(new[] { CodacyParityStandards.CodeMetricsPath });
	}

	[Fact]
	public async Task Cq10_ShouldPass_WhenTheParityBuildIsInPlace()
	{
		var files = Files("App/App.csproj", CodacyParityStandards.TargetsPath, CodacyParityStandards.GlobalConfigPath, CodacyParityStandards.CodeMetricsPath);
		files[CodacyParityStandards.TargetsPath] = CodacyParityStandards.TargetsContent;

		var result = await Evaluate(files).ConfigureAwait(true);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task Cq10_ShouldNotOverwriteAnExistingDirectoryBuildTargets()
	{
		var files = Files("App/App.csproj", CodacyParityStandards.TargetsPath);
		files[CodacyParityStandards.TargetsPath] = "<Project><PropertyGroup><Mine>true</Mine></PropertyGroup></Project>";

		var result = await Evaluate(files).ConfigureAwait(true);

		result.Passed.Should().BeFalse();
		result.Advisory!.Data.Should().NotContainKey("remediation_type");
	}

	[Fact]
	public void GeneratedConfiguration_ShouldTurnEverythingOffAndOnlyTheCodacyRulesOn()
	{
		var config = CodacyParityStandards.GlobalConfigContent;

		CodacyParityStandards.SonarRuleIds.Should().HaveCountGreaterThan(100).And.Contain(["S2339", "S2360", "S1172", "S1118"]);
		config.Should().Contain("dotnet_analyzer_diagnostic.severity = none")
			.And.Contain("dotnet_diagnostic.S2339.severity = warning")
			.And.Contain("dotnet_diagnostic.CA1502.severity = warning")
			.And.Contain("dotnet_diagnostic.MA0051.severity = warning")
			.And.Contain("MA0051.maximum_lines_per_method = 50");
		config.Split('\n').Count(line => line.EndsWith("= warning", StringComparison.Ordinal)).Should().Be(CodacyParityStandards.SonarRuleIds.Count + 2);
	}

	[Fact]
	public void Targets_ShouldBeOptInAndWarningsOnly()
	{
		var targets = CodacyParityStandards.TargetsContent;

		targets.Should().Contain("'$(CodacyParity)' == 'true'").And.Contain("BeforeTargets=\"CoreCompile\"");
		targets.Should().Contain("<TreatWarningsAsErrors>false</TreatWarningsAsErrors>").And.NotContain("<TreatWarningsAsErrors>true");
		targets.Should().Contain("VersionOverride=").And.Contain("Version=\"");
	}

	[Fact]
	public async Task Fix_ShouldCreateTheFiles_AndLeaveExistingOnesAlone()
	{
		Write("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		Write(CodacyParityStandards.CodeMetricsPath, "CA1502: 5\n");

		var context = new LocalRepositoryContextBuilder(NullLogger<LocalRepositoryContextBuilder>.Instance)
			.Build(_root, "panoramicdata/SomeRepo", new RepoOptions(), new LocalContextFacts());
		var result = await new CodacyParityBuildRule().EvaluateAsync(context, CancellationToken.None).ConfigureAwait(true);
		var applied = new List<string>();

		new CodacyParityBuildRemediation().Apply(_root, result, applied, null);

		File.ReadAllText(Path.Combine(_root, CodacyParityStandards.TargetsPath)).Should().Be(CodacyParityStandards.TargetsContent);
		File.ReadAllText(Path.Combine(_root, CodacyParityStandards.GlobalConfigPath)).Should().Be(CodacyParityStandards.GlobalConfigContent);
		File.ReadAllText(Path.Combine(_root, CodacyParityStandards.CodeMetricsPath)).Should().Be("CA1502: 5\n");
		applied.Should().BeEquivalentTo(new[] { CodacyParityStandards.TargetsPath, CodacyParityStandards.GlobalConfigPath });
	}

	public void Dispose()
	{
		GC.SuppressFinalize(this);

		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	private static Dictionary<string, string> Files(params string[] paths)
		=> paths.ToDictionary(path => path, _ => string.Empty);

	private static async Task<RuleResult> Evaluate(Dictionary<string, string> files)
	{
		var context = new RepositoryContext
		{
			FullName = "test-org/test-repo",
			Name = "test-repo",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = [.. files.Keys],
			FileContents = files.Where(file => file.Value.Length > 0).ToDictionary(file => file.Key, file => file.Value),
			RepositoryConfig = null
		};

		return await RuleRegistry.Rules.Single(rule => rule.RuleId == "CQ-10")
			.EvaluateAsync(context, CancellationToken.None).ConfigureAwait(true);
	}

	private void Write(string relativePath, string content)
	{
		var path = Path.Combine(_root, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
	}
}
