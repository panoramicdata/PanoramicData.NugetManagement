using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Web.Remediations.Testing;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// End-to-end tests for the TST-08 remediation: the rule's advisory, applied to a real directory.
/// The swap only works if the package manifests and the source files move together, so these assert
/// against files on disk rather than against the shape of the advisory.
/// </summary>
public class AwesomeAssertionsRemediationTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"nm-tst08-" + Guid.NewGuid().ToString("N"));

	[Fact]
	public async Task Apply_ShouldRepinVersionsAndRewriteBothManifestsAndSources()
	{
		Write("Directory.Packages.props", """
			<Project>
			  <ItemGroup>
			    <PackageVersion Include="FluentAssertions" Version="8.10.0" />
			    <PackageVersion Include="FluentAssertions.Analyzers" Version="0.34.1" />
			  </ItemGroup>
			</Project>
			""");
		Write("Acme.Test/Acme.Test.csproj", """<Project><ItemGroup><PackageReference Include="FluentAssertions" /></ItemGroup></Project>""");
		Write("Acme.Test/WidgetTests.cs", "using FluentAssertions;\n\npublic class WidgetTests { }");

		var applied = await ApplyAsync().ConfigureAwait(true);

		var props = Read("Directory.Packages.props");
		props.Should().Contain("""<PackageVersion Include="AwesomeAssertions" Version="9.6.0" />""");
		props.Should().Contain("""<PackageVersion Include="AwesomeAssertions.Analyzers" Version="9.0.8" />""");

		Read("Acme.Test/Acme.Test.csproj").Should().Contain("""Include="AwesomeAssertions" """.TrimEnd());
		Read("Acme.Test/WidgetTests.cs").Should().StartWith("using AwesomeAssertions;");

		applied.Should().HaveCount(3);
	}

	[Fact]
	public async Task Apply_ShouldLeaveBuildOutputAlone()
	{
		// bin and obj hold generated and copied sources. Rewriting them changes nothing that gets
		// committed, and makes the applied list overstate what was touched.
		Write("Directory.Packages.props", """<Project><ItemGroup><PackageVersion Include="FluentAssertions" Version="8.10.0" /></ItemGroup></Project>""");
		Write("Acme.Test/obj/Debug/Generated.cs", "using FluentAssertions;");
		Write("Acme.Test/bin/Debug/Copied.cs", "using FluentAssertions;");

		var applied = await ApplyAsync().ConfigureAwait(true);

		Read("Acme.Test/obj/Debug/Generated.cs").Should().Contain("FluentAssertions");
		Read("Acme.Test/bin/Debug/Copied.cs").Should().Contain("FluentAssertions");
		applied.Should().ContainSingle().Which.Should().Be("Directory.Packages.props");
	}

	private async Task<List<string>> ApplyAsync()
	{
		var context = new RepositoryContext
		{
			FullName = "test-org/Acme",
			Name = "Acme",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = ["Directory.Packages.props", "Acme.Test/Acme.Test.csproj"],
			FileContents = new Dictionary<string, string>
			{
				["Directory.Packages.props"] = Read("Directory.Packages.props"),
				["Acme.Test/Acme.Test.csproj"] = "<Project><ItemGroup /></Project>"
			}
		};

		var result = await new AwesomeAssertionsRule().EvaluateAsync(context, CancellationToken.None).ConfigureAwait(false);
		result.Passed.Should().BeFalse("the fixture references FluentAssertions");

		var remediation = new AwesomeAssertionsRemediation();
		remediation.CanRemediate(result).Should().BeTrue();

		var applied = new List<string>();
		remediation.Apply(_root, result, applied, Output.WriteLine);
		return applied;
	}

	private void Write(string relativePath, string content)
	{
		var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		File.WriteAllText(full, content);
	}

	private string Read(string relativePath)
		=> File.ReadAllText(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

	/// <inheritdoc />
	public void Dispose()
	{
		GC.SuppressFinalize(this);

		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}
}
