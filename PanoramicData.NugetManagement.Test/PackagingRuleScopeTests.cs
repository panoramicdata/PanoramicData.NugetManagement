using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that the packaging rules follow the projects a repository actually publishes, rather than a
/// project named after the repository — the assumption behind both halves of issue 23.
/// </summary>
public class PackagingRuleScopeTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _publishedButBare =
		"<Project><PropertyGroup><GeneratePackageOnBuild>true</GeneratePackageOnBuild></PropertyGroup></Project>";

	private const string _toolProject =
		"<Project><PropertyGroup><PackageId>Acme.Tool</PackageId><PackAsTool>true</PackAsTool>"
		+ "<ToolCommandName>acme</ToolCommandName><OutputType>Exe</OutputType></PropertyGroup></Project>";

	private const string _ordinaryPackage =
		"<Project><PropertyGroup><PackageId>Acme.Lib</PackageId></PropertyGroup></Project>";

	private const string _sampleApp =
		"<Project><PropertyGroup><OutputType>Exe</OutputType><IsPackable>false</IsPackable></PropertyGroup></Project>";

	[Fact]
	public async Task META04_ShouldCheckTheProjectThatIsPublished_WhateverItIsNamed()
	{
		// ConnectWise.Api: the package project is not named after the repository. This used to pass
		// without checking anything.
		var context = CreateContext("ConnectWise.Api", new Dictionary<string, string>
		{
			["ConnectWise.Api/ConnectWise.Manage.Api.csproj"] = _publishedButBare
		});

		var result = await GetRule("META-04").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("ConnectWise.Manage.Api.csproj");
	}

	[Fact]
	public async Task PKG09_ShouldNotDemandNonPackableOfTheProjectThatIsPublished()
	{
		// The other half of issue 23: with no name match, the repository's own package counted as an
		// ancillary project and was told to set IsPackable=false.
		var context = CreateContext("ConnectWise.Api", new Dictionary<string, string>
		{
			["ConnectWise.Api/ConnectWise.Manage.Api.csproj"] = _publishedButBare
		});

		var result = await GetRule("PKG-09").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue("the published project is not an ancillary project");
	}

	[Fact]
	public async Task PKG09_ShouldStillDemandNonPackableOfASampleApp()
	{
		var context = CreateContext("Acme.Widget", new Dictionary<string, string>
		{
			["Acme.Widget/Acme.Widget.csproj"] = _publishedButBare,
			["ExampleApp/ExampleApp.csproj"] = "<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>"
		});

		var result = await GetRule("PKG-09").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("ExampleApp");
	}

	[Fact]
	public async Task META04_ShouldReportEveryPublishedProjectInAMultiPackageRepository()
	{
		var context = CreateContext("PanoramicData.HealthChecks", new Dictionary<string, string>
		{
			["Core/PanoramicData.HealthChecks.Core.csproj"] = _publishedButBare,
			["BasicAuth/PanoramicData.HealthChecks.BasicAuthentication.csproj"] = _publishedButBare,
			["Sample/Sample.csproj"] = _sampleApp
		});

		var result = await GetRule("META-04").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("Core");
		result.Message.Should().Contain("BasicAuthentication");
		result.Message.Should().NotContain("Sample", "a project that opts out is not published");
	}

	[Theory]
	[InlineData("META-01")]
	[InlineData("META-02")]
	[InlineData("META-04")]
	[InlineData("LIC-02")]
	[InlineData("PKG-01")]
	[InlineData("PKG-02")]
	[InlineData("PKG-03")]
	public async Task PackagingRules_ShouldReportNotApplicable_WhenNoProjectDeclaresItselfPublished(string ruleId)
	{
		var context = CreateContext("Acme.Widget", new Dictionary<string, string>
		{
			["Src/Widget.csproj"] = "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
		});

		var result = await GetRule(ruleId).EvaluateAsync(context, CancellationToken.None);

		result.IsApplicable.Should().BeFalse("a rule with nothing to check must not read as compliance");
	}

	[Fact]
	public async Task PKG10_ShouldFail_WhenNothingDeclaresItselfPublished()
	{
		var context = CreateContext("Acme.Widget", new Dictionary<string, string>
		{
			["Src/Widget.csproj"] = "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
			["Tools/Importer.csproj"] = "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
		});

		var result = await GetRule("PKG-10").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Advisory!.Data["candidates"].Should().BeEquivalentTo(new[] { "Src/Widget.csproj", "Tools/Importer.csproj" });
	}

	[Fact]
	public async Task PKG10_ShouldPass_WhenAProjectDeclaresItself()
	{
		var context = CreateContext("Acme.Widget", new Dictionary<string, string>
		{
			["Src/Widget.csproj"] = _publishedButBare
		});

		var result = await GetRule("PKG-10").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task PKG10_ShouldPass_WhenTheRepositoryPublishesNothing()
	{
		var context = CreateContext("Acme.App", new Dictionary<string, string>
		{
			["Src/App.csproj"] = "<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>"
		}, new RepoOptions { IsPackable = false });

		var result = await GetRule("PKG-10").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task PKG02_ShouldNotDemandGeneratePackageOnBuild_OfAToolProject()
	{
		// Issue 71: a tool project is packed via publish. GeneratePackageOnBuild makes packing run
		// during Build, before that publish output exists, so dotnet pack fails with MSB3030 and
		// produces no package at all. IsPackableProject counts PackAsTool as evidence a project is
		// published, so without this exclusion the rule pulls tool projects in and then breaks them.
		var context = CreateContext("Acme.Tool", new Dictionary<string, string>
		{
			["Acme.Tool/Acme.Tool.csproj"] = _toolProject
		});

		var result = await GetRule("PKG-02").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue("a tool project must not be told to enable GeneratePackageOnBuild");
	}

	[Fact]
	public async Task PKG02_ShouldStillDemandGeneratePackageOnBuild_OfAnOrdinaryPackageProject()
	{
		// Guards the exclusion above against over-reaching.
		var context = CreateContext("Acme.Lib", new Dictionary<string, string>
		{
			["Acme.Lib/Acme.Lib.csproj"] = _ordinaryPackage
		});

		var result = await GetRule("PKG-02").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("Acme.Lib");
	}

	[Fact]
	public async Task PKG02_ShouldFlagOnlyTheNonToolProject_InAMixedRepository()
	{
		var context = CreateContext("Acme.Suite", new Dictionary<string, string>
		{
			["Tool/Acme.Tool.csproj"] = _toolProject,
			["Lib/Acme.Lib.csproj"] = _ordinaryPackage
		});

		var result = await GetRule("PKG-02").EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("Acme.Lib.csproj");
		result.Message.Should().NotContain("Acme.Tool.csproj");
	}

	private static IRule GetRule(string ruleId)
		=> RuleRegistry.Rules.First(r => r.RuleId == ruleId);

	private static RepositoryContext CreateContext(
		string name,
		Dictionary<string, string> files,
		RepoOptions? options = null) => new()
		{
			FullName = $"test-org/{name}",
			Name = name,
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = options ?? new RepoOptions(),
			FilePaths = [.. files.Keys],
			FileContents = files
		};
}
