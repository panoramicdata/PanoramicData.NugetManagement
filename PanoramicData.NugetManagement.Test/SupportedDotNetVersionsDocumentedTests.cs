using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that DOC-02 requires the set phrase in README.md and rejects every other .NET version mention.
/// </summary>
public class SupportedDotNetVersionsDocumentedTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static string Project(string tfm, string description = "A library.")
		=> $"<Project><PropertyGroup><TargetFramework>{tfm}</TargetFramework><PackageId>Acme.Lib</PackageId><Description>{description}</Description></PropertyGroup></Project>";

	[Fact]
	public async Task ShouldPass_WhenReadmeHasThePhraseAndNothingElse()
	{
		var result = await Evaluate(new()
		{
			["Acme.Lib/Acme.Lib.csproj"] = Project("net11.0"),
			["README.md"] = "# Acme.Lib\n\nSupports .NET 11.0.\n"
		});

		result.Passed.Should().BeTrue(result.Message);
	}

	[Fact]
	public async Task ShouldFail_WhenThePhraseIsMissing()
	{
		var result = await Evaluate(new()
		{
			["Acme.Lib/Acme.Lib.csproj"] = Project("net11.0"),
			["README.md"] = "# Acme.Lib\n\nA library.\n"
		});

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("Supports .NET 11.0");
	}

	[Theory]
	[InlineData(".NET 10 library for talking to things.")]
	[InlineData("Targets net10.0.")]
	[InlineData("Needs .NET 8+ to run.")]
	[InlineData("Also runs on .NET Framework 4.8.")]
	[InlineData("Works with .NET Standard 2.0.")]
	[InlineData("Legacy .NET Core 3.1 support.")]
	public async Task ShouldFail_WhenAnotherVersionIsMentionedInAnyMarkdownFile(string stale)
	{
		var result = await Evaluate(new()
		{
			["Acme.Lib/Acme.Lib.csproj"] = Project("net11.0"),
			["README.md"] = "Supports .NET 11.0.\n",
			["CONTRIBUTING.md"] = stale
		});

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("CONTRIBUTING.md");
	}

	[Fact]
	public async Task ShouldFail_WhenThePackageDescriptionNamesAnotherVersion()
	{
		var result = await Evaluate(new()
		{
			["Acme.Lib/Acme.Lib.csproj"] = Project("net11.0", ".NET 10 library for talking to things."),
			["README.md"] = "Supports .NET 11.0.\n"
		});

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("<Description>");
	}

	[Fact]
	public async Task ShouldRequireEveryTargetedVersion_WhenMultiTargeting()
	{
		var files = new Dictionary<string, string>
		{
			["Acme.Lib/Acme.Lib.csproj"] = Project("net8.0;net11.0").Replace("<TargetFramework>", "<TargetFrameworks>").Replace("</TargetFramework>", "</TargetFrameworks>"),
			["README.md"] = "Supports .NET 8.0 and .NET 11.0.\nBuilt on .NET 11.\n"
		};

		(await Evaluate(files)).Passed.Should().BeTrue();

		files["README.md"] = "Supports .NET 11.0.\n";
		(await Evaluate(files)).Passed.Should().BeFalse();
	}

	[Fact]
	public async Task ShouldInheritTargetFrameworkFromDirectoryBuildProps()
	{
		var result = await Evaluate(new()
		{
			["Directory.Build.props"] = "<Project><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>",
			["Acme.Lib/Acme.Lib.csproj"] = "<Project><PropertyGroup><PackageId>Acme.Lib</PackageId></PropertyGroup></Project>",
			["README.md"] = "Supports .NET 11.0.\n"
		});

		result.Passed.Should().BeTrue(result.Message);
	}

	[Fact]
	public async Task ShouldIgnoreDatedPlansButCheckNestedReadmes()
	{
		var files = new Dictionary<string, string>
		{
			["Acme.Lib/Acme.Lib.csproj"] = Project("net11.0"),
			["README.md"] = "Supports .NET 11.0.\n",
			["docs/plans/2026-08-28-plan.md"] = "Built for .NET 10.",
			["CHANGELOG.md"] = "- Added .NET 9 support.",
			["MASTER_PLAN.md"] = "Target: .NET 10."
		};

		(await Evaluate(files).ConfigureAwait(true)).Passed.Should().BeTrue();

		files["Acme.Lib/README.md"] = "Built for .NET 10.";
		(await Evaluate(files).ConfigureAwait(true)).Passed.Should().BeFalse();
	}

	[Fact]
	public async Task ShouldNotApply_WhenNothingIsPublished()
	{
		var result = await Evaluate(new()
		{
			["Tool/Tool.csproj"] = "<Project><PropertyGroup><TargetFramework>net11.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup></Project>",
			["README.md"] = "Nothing.\n"
		});

		result.IsApplicable.Should().BeFalse();
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

		return await RuleRegistry.Rules.Single(r => r.RuleId == "DOC-02")
			.EvaluateAsync(context, CancellationToken.None)
			.ConfigureAwait(false);
	}
}
