using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for TST-08, which bans FluentAssertions in favour of AwesomeAssertions. FluentAssertions 8
/// requires a paid Xceed licence; AwesomeAssertions is the API-compatible fork that does not.
/// </summary>
public class AwesomeAssertionsRuleTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _testProjectPath = "Acme.Lib.Test/Acme.Lib.Test.csproj";

	[Fact]
	public async Task TST08_ShouldFail_WhenDirectoryPackagesPropsPinsFluentAssertions()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] =
				"""<Project><ItemGroup><PackageVersion Include="FluentAssertions" Version="8.10.0" /></ItemGroup></Project>""",
			[_testProjectPath] = "<Project><ItemGroup /></Project>"
		});

		var result = await new AwesomeAssertionsRule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("FluentAssertions");
	}

	[Fact]
	public async Task TST08_ShouldFail_WhenATestProjectReferencesFluentAssertions()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = "<Project><ItemGroup /></Project>",
			[_testProjectPath] =
				"""<Project><ItemGroup><PackageReference Include="FluentAssertions" /></ItemGroup></Project>"""
		});

		var result = await new AwesomeAssertionsRule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
	}

	[Fact]
	public async Task TST08_ShouldFail_WhenOnlyTheAnalyzersPackageIsReferenced()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] =
				"""<Project><ItemGroup><PackageVersion Include="FluentAssertions.Analyzers" Version="0.34.1" /></ItemGroup></Project>""",
			[_testProjectPath] = "<Project><ItemGroup /></Project>"
		});

		var result = await new AwesomeAssertionsRule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
	}

	[Fact]
	public async Task TST08_ShouldBeAnError_BecauseFluentAssertionsNeedsAPaidLicence()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] =
				"""<Project><ItemGroup><PackageVersion Include="FluentAssertions" Version="8.10.0" /></ItemGroup></Project>""",
			[_testProjectPath] = "<Project><ItemGroup /></Project>"
		});

		var result = await new AwesomeAssertionsRule().EvaluateAsync(context, CancellationToken.None);

		result.Severity.Should().Be(AssessmentSeverity.Error);
	}

	[Fact]
	public async Task TST08_ShouldPass_WhenAwesomeAssertionsIsUsedInstead()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] =
				"""<Project><ItemGroup><PackageVersion Include="AwesomeAssertions" Version="9.6.0" /></ItemGroup></Project>""",
			[_testProjectPath] =
				"""<Project><ItemGroup><PackageReference Include="AwesomeAssertions" /></ItemGroup></Project>"""
		});

		var result = await new AwesomeAssertionsRule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task TST08_ShouldPass_WhenTheRepositoryUsesNoAssertionLibraryAtAll()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = "<Project><ItemGroup /></Project>",
			[_testProjectPath] = "<Project><ItemGroup /></Project>"
		});

		var result = await new AwesomeAssertionsRule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task TST08_ShouldRemediateAcrossManifestsAndSources()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] =
				"""<Project><ItemGroup><PackageVersion Include="FluentAssertions" Version="8.10.0" /></ItemGroup></Project>""",
			[_testProjectPath] = "<Project><ItemGroup /></Project>"
		});

		var result = await new AwesomeAssertionsRule().EvaluateAsync(context, CancellationToken.None);

		var data = result.Advisory!.Data;
		data["remediation_type"].Should().Be("replace_regex_in_files");

		// The .cs sweep is what turns `using FluentAssertions;` into `using AwesomeAssertions;`;
		// without it the swap compiles nothing.
		var globs = (string[])data["globs"];
		globs.Should().Contain("Directory.Packages.props");
		globs.Should().Contain("**/*.csproj");
		globs.Should().Contain("**/*.cs");

		var patterns = (string[])data["patterns"];
		var replacements = (string[])data["replacements"];
		patterns.Should().HaveSameCount(replacements);

		// The Analyzers pin must be rewritten before the bare name, or the bare rule renames it
		// first and the Analyzers pattern no longer matches, leaving its version wrong.
		var analyzerIndex = patterns.ToList().FindIndex(p => p.Contains("FluentAssertions.Analyzers"));
		var bareIndex = patterns.ToList().FindIndex(p => p == "FluentAssertions");
		analyzerIndex.Should().BeGreaterThanOrEqualTo(0);
		bareIndex.Should().BeGreaterThan(analyzerIndex);
	}

	private static RepositoryContext CreateContext(Dictionary<string, string> files) => new()
	{
		FullName = "test-org/Acme.Lib",
		Name = "Acme.Lib",
		DefaultBranch = "main",
		CurrentBranch = "main",
		Options = new RepoOptions(),
		FilePaths = [.. files.Keys],
		FileContents = files
	};
}
