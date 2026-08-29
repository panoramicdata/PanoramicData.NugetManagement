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
	public async Task TST08_ShouldFail_WhenPinnedCentrallyEvenWithNoTestProject()
	{
		// The licence obligation attaches to the pin, not to where it is consumed. Gating on the
		// presence of a test project also let the rule report itself not-applicable against the
		// FailArmy fixture, which silently excused it from the "every rule must fail" invariant.
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] =
				"""<Project><ItemGroup><PackageVersion Include="FluentAssertions" Version="8.10.0" /></ItemGroup></Project>"""
		});

		var result = await new AwesomeAssertionsRule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.IsApplicable.Should().BeTrue();
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
	}

	[Fact]
	public void TST08_Remediation_ShouldRepinVersions_BecauseTheFluentAssertionsVersionsDoNotExist()
	{
		// AwesomeAssertions has no 8.10.0 (it runs 7.0.0 to 9.6.0), so renaming the package without
		// repinning the version produces a reference that cannot restore.
		var props = """
			<Project>
			  <ItemGroup>
			    <PackageVersion Include="FluentAssertions" Version="8.10.0" />
			    <PackageVersion Include="FluentAssertions.Analyzers" Version="0.34.1" />
			  </ItemGroup>
			</Project>
			""";

		var rewritten = ApplyRemediation(props);

		rewritten.Should().Contain("""<PackageVersion Include="AwesomeAssertions" Version="9.6.0" />""");
		rewritten.Should().Contain("""<PackageVersion Include="AwesomeAssertions.Analyzers" Version="9.0.8" />""");
		rewritten.Should().NotContain("8.10.0");
		rewritten.Should().NotContain("0.34.1");
	}

	[Fact]
	public void TST08_Remediation_ShouldRewriteUsingDirectivesAndPackageReferences()
	{
		ApplyRemediation("global using FluentAssertions;")
			.Should().Be("global using AwesomeAssertions;");

		ApplyRemediation("""<PackageReference Include="FluentAssertions.Analyzers" />""")
			.Should().Be("""<PackageReference Include="AwesomeAssertions.Analyzers" />""");
	}

	/// <summary>
	/// Applies the rule's own patterns in the order it emits them, which is what the
	/// replace_regex_in_files remediation does to each matching file.
	/// </summary>
	private static string ApplyRemediation(string content)
	{
		var advisory = new AwesomeAssertionsRule()
			.EvaluateAsync(
				CreateContext(new Dictionary<string, string>
				{
					["Directory.Packages.props"] =
						"""<Project><ItemGroup><PackageVersion Include="FluentAssertions" Version="8.10.0" /></ItemGroup></Project>""",
					[_testProjectPath] = "<Project><ItemGroup /></Project>"
				}),
				CancellationToken.None)
			.GetAwaiter()
			.GetResult()
			.Advisory!;

		var patterns = (string[])advisory.Data["patterns"];
		var replacements = (string[])advisory.Data["replacements"];

		for (var i = 0; i < patterns.Length; i++)
		{
			content = System.Text.RegularExpressions.Regex.Replace(content, patterns[i], replacements[i]);
		}

		return content;
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
