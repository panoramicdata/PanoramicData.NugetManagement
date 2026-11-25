using System.Text;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// LIC-05 ignores project files that live in test-fixture folders inside the assessed repository:
/// they are deliberately non-conformant data, not part of what the repository publishes.
/// </summary>
public class CopyrightOverrideFixtureTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _overridingProject = "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\t<PropertyGroup>\r\n\t\t<Copyright>Panoramic Data Limited 2025</Copyright>\r\n\t</PropertyGroup>\r\n</Project>\r\n";
	private const string _fixtureProject = "Acme.Lib.Test/Fixtures/Sample/Sample.csproj";
	private const string _realProject = "Acme.Lib/Acme.Lib.csproj";

	[Theory]
	[InlineData("Acme.Lib.Test/Fixtures/Sample/Sample.csproj", true)]
	[InlineData("Acme.Lib.Test/fixture/Sample.csproj", true)]
	[InlineData("Acme.Lib.Test/TestData/Sample.csproj", true)]
	[InlineData("tests\\testfiles\\Sample.csproj", true)]
	[InlineData("a/TestAssets/b/Sample.csproj", true)]
	[InlineData("Acme.Lib/Acme.Lib.csproj", false)]
	[InlineData("Acme.Lib.Test/Acme.Lib.Test.csproj", false)]
	[InlineData("Acme.Fixtures/Acme.Fixtures.csproj", false)]
	[InlineData("Fixtures.csproj", false)]
	public void GovernedPaths_JudgesWholeSegments(string path, bool isFixture)
		=> GovernedPaths.IsTestFixture(path).Should().Be(isFixture);

	[Fact]
	public async Task Rule_IgnoresAFixtureProject_AndNeitherFailsNorListsIt()
	{
		var result = await Evaluate(new() { [_fixtureProject] = _overridingProject, [_realProject] = "<Project />" });

		result.Passed.Should().BeTrue(result.Message);
		result.Message.Should().NotContain("Sample.csproj");
	}

	[Fact]
	public async Task Rule_IsNotApplicable_WhenEveryProjectIsAFixture()
	{
		var result = await Evaluate(new() { [_fixtureProject] = _overridingProject });

		result.IsApplicable.Should().BeFalse();
	}

	[Fact]
	public async Task Rule_StillFailsARealProject_BesideAFixtureProject()
	{
		var result = await Evaluate(new() { [_fixtureProject] = _overridingProject, [_realProject] = _overridingProject });

		result.Passed.Should().BeFalse();
		(result.Advisory!.Data["projects"] as string[]).Should().Equal(_realProject);
		result.Message.Should().NotContain("Sample.csproj");
	}

	[Fact]
	public async Task Rule_StillFails_WhenTheRepositoryRootIsTheFixtureItself()
	{
		var result = await Evaluate(new() { ["PanoramicData.NugetFailArmy/PanoramicData.NugetFailArmy.csproj"] = _overridingProject });

		result.Passed.Should().BeFalse();
	}

	[Fact]
	public async Task Remediation_LeavesAFixtureProjectUntouched_EvenWhenACraftedAdvisoryListsIt()
	{
		var root = NewRoot();
		try
		{
			var fixture = Path.Combine(root, "Acme.Lib.Test", "Fixtures", "Sample", "Sample.csproj");
			var real = Path.Combine(root, "Acme.Lib", "Acme.Lib.csproj");
			Directory.CreateDirectory(Path.GetDirectoryName(fixture)!);
			Directory.CreateDirectory(Path.GetDirectoryName(real)!);
			var bytes = Encoding.UTF8.GetBytes(_overridingProject);
			File.WriteAllBytes(fixture, bytes);
			File.WriteAllBytes(real, bytes);

			var failed = await Evaluate(new() { [_realProject] = _overridingProject });
			failed.Advisory!.Data["projects"] = new[] { _fixtureProject, "Acme.Lib.Test\\Fixtures\\Sample\\Sample.csproj", _realProject };

			var applied = new List<string>();
			new RemediationRegistry().Get("LIC-05")!.Apply(root, failed, applied, null);

			File.ReadAllBytes(fixture).Should().Equal(bytes);
			File.ReadAllText(real).Should().NotContain("<Copyright>");
			applied.Should().Equal(_realProject);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public async Task Remediation_LeavesThirdPartyValuesAlone()
	{
		var root = NewRoot();
		try
		{
			var real = Path.Combine(root, "Acme.Lib", "Acme.Lib.csproj");
			Directory.CreateDirectory(Path.GetDirectoryName(real)!);
			var thirdParty = _overridingProject.Replace("Panoramic Data Limited 2025", "Copyright © Acme Corp", StringComparison.Ordinal);
			var bytes = Encoding.UTF8.GetBytes(thirdParty);
			File.WriteAllBytes(real, bytes);

			var failed = await Evaluate(new() { [_realProject] = _overridingProject });
			new RemediationRegistry().Get("LIC-05")!.Apply(root, failed, [], null);

			File.ReadAllBytes(real).Should().Equal(bytes);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private static string NewRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "lic05fx-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	private static async Task<RuleResult> Evaluate(Dictionary<string, string> files)
	{
		var context = new RepositoryContext
		{
			FullName = "test-org/Acme.Lib",
			Name = "Acme.Lib",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = [.. files.Keys],
			FileContents = files
		};

		return await new CopyrightOverrideRule().EvaluateAsync(context, CancellationToken.None).ConfigureAwait(false);
	}
}
