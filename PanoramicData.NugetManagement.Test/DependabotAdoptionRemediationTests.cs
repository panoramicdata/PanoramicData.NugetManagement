using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="DependabotAdoptionRemediation"/>: it turns an adoption plan into file
/// rewrites, and reports what it wrote so the runner knows whether closing the pull request is
/// honest.
/// </summary>
public class DependabotAdoptionRemediationTests(ITestOutputHelper output) : TestWithOutput(output)
{
	/// <summary>A throwaway working tree, seeded with the given files.</summary>
	private static string Tree(params (string RelativePath, string Content)[] files)
	{
		var root = Path.Combine(Path.GetTempPath(), "adopt-" + Guid.NewGuid().ToString("N"));

		foreach (var (relativePath, content) in files)
		{
			var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(full)!);
			File.WriteAllText(full, content);
		}

		return root;
	}

	private static string Read(string root, string relativePath)
		=> File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

	[Fact]
	public void Adopt_APackageUpdate_RewritesTheDeclaredVersion()
	{
		var root = Tree(("Directory.Packages.props", """
			<Project><ItemGroup><PackageVersion Include="coverlet.collector" Version="8.0.1" /></ItemGroup></Project>
			"""));

		try
		{
			var written = DependabotAdoptionRemediation.Adopt(
				root,
				new DependabotAdoptionPlan(
					["Directory.Packages.props|coverlet.collector|PackageVersionAttribute|8.0.1|10.0.0"],
					[],
					[]),
				Output.WriteLine);

			written.Should().NotBeEmpty("the file was there to write");
			Read(root, "Directory.Packages.props")
				.Should().Contain(@"Version=""10.0.0""").And.NotContain(@"Version=""8.0.1""");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Adopt_AnActionUpdate_RewritesEveryUsesLine()
	{
		var root = Tree((".github/workflows/ci.yml", """
			jobs:
			  build:
			    steps:
			    - uses: actions/checkout@v4
			    - uses: actions/checkout@v4
			"""));

		try
		{
			var written = DependabotAdoptionRemediation.Adopt(
				root,
				new DependabotAdoptionPlan(
					[],
					[ActionUsesPattern.Below("actions/checkout", "5")],
					[ActionUsesPattern.Replacement("5")]),
				Output.WriteLine);

			written.Should().NotBeEmpty();
			Read(root, ".github/workflows/ci.yml")
				.Should().NotContain("actions/checkout@v4")
				.And.Contain("actions/checkout@v5");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Adopt_WhenTheFileIsNotThere_WritesNothingAndSaysSo()
	{
		var root = Tree(("README.md", "nothing to rewrite here"));

		try
		{
			DependabotAdoptionRemediation
				.Adopt(
					root,
					new DependabotAdoptionPlan(
						["Directory.Packages.props|coverlet.collector|PackageVersionAttribute|8.0.1|10.0.0"],
						[],
						[]),
					Output.WriteLine)
				.Should().BeEmpty(
					"reporting nothing written is what stops the runner closing a pull request against no "
						+ "change at all");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Adopt_WhenTheDeclaredVersionHasAlreadyMoved_WritesNothing()
	{
		var root = Tree(("Directory.Packages.props", """
			<Project><ItemGroup><PackageVersion Include="coverlet.collector" Version="10.0.0" /></ItemGroup></Project>
			"""));

		try
		{
			DependabotAdoptionRemediation
				.Adopt(
					root,
					new DependabotAdoptionPlan(
						["Directory.Packages.props|coverlet.collector|PackageVersionAttribute|8.0.1|10.0.0"],
						[],
						[]),
					Output.WriteLine)
				.Should().BeEmpty(
					"two pull requests in one pass can propose the same package, and the second must not "
						+ "report a write it did not make");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Adopt_APlanWithBothKinds_AppliesBoth()
	{
		var root = Tree(
			("Directory.Packages.props", """
				<Project><ItemGroup><PackageVersion Include="Serilog" Version="3.0.0" /></ItemGroup></Project>
				"""),
			(".github/workflows/ci.yml", "jobs:\n  build:\n    steps:\n    - uses: actions/checkout@v4\n"));

		try
		{
			var written = DependabotAdoptionRemediation.Adopt(
				root,
				new DependabotAdoptionPlan(
					["Directory.Packages.props|Serilog|PackageVersionAttribute|3.0.0|4.0.0"],
					[ActionUsesPattern.Below("actions/checkout", "5")],
					[ActionUsesPattern.Replacement("5")]),
				Output.WriteLine);

			written.Should().HaveCountGreaterThanOrEqualTo(2, "both halves of the plan had work to do");
			Read(root, "Directory.Packages.props").Should().Contain(@"Version=""4.0.0""");
			Read(root, ".github/workflows/ci.yml").Should().Contain("actions/checkout@v5");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Adopt_NeverLowersAVersionThatIsAlreadyAhead()
	{
		var root = Tree((".github/workflows/ci.yml", "    - uses: actions/checkout@v9\n"));

		try
		{
			DependabotAdoptionRemediation.Adopt(
				root,
				new DependabotAdoptionPlan(
					[],
					[ActionUsesPattern.Below("actions/checkout", "5")],
					[ActionUsesPattern.Replacement("5")]),
				Output.WriteLine);

			Read(root, ".github/workflows/ci.yml").Should().Contain(
				"actions/checkout@v9",
				"a repository ahead of what the pull request proposes must never be dragged back");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}
}
