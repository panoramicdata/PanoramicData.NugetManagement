using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="DeclaredRepositoryUrlRule"/> (META-06), which holds a package's declared
/// URLs to the repository's real name.
/// </summary>
public class DeclaredRepositoryUrlRuleTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public async Task Passes_WhenTheDeclaredUrlIsCanonical()
	{
		var result = await Evaluate(Csproj(
			repositoryUrl: "https://github.com/panoramicdata/Dell.CloudIq.Api",
			packageProjectUrl: "https://github.com/panoramicdata/Dell.CloudIq.Api"));

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task Fails_WhenTheDeclaredUrlDiffersOnlyByCase()
	{
		// The failure this whole rule exists for: GitHub routes it, so nothing complains, and Codacy
		// then answers 404 for a repository it holds.
		var result = await Evaluate(Csproj(
			repositoryUrl: "https://github.com/panoramicdata/Dell.CloudIQ.Api",
			packageProjectUrl: "https://github.com/panoramicdata/Dell.CloudIq.Api"));

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("Dell.CloudIQ.Api");
		result.Advisory!.Data["remediation_type"].Should().Be("replace_regex_in_files");
	}

	[Fact]
	public async Task Fails_WhenTheDeclaredUrlNamesAnotherRepository()
	{
		var result = await Evaluate(Csproj(
			repositoryUrl: "https://github.com/panoramicdata/MicrosoftAzureSentinel.Api",
			packageProjectUrl: null));

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("MicrosoftAzureSentinel.Api");
	}

	[Fact]
	public async Task Fails_WhenPackageProjectUrlDiffersOnlyByCase()
	{
		var result = await Evaluate(Csproj(
			repositoryUrl: "https://github.com/panoramicdata/Dell.CloudIq.Api",
			packageProjectUrl: "https://github.com/PanoramicData/Dell.CloudIq.Api"));

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("PackageProjectUrl");
	}

	[Fact]
	public async Task Passes_WhenPackageProjectUrlPointsSomewhereElseEntirely()
	{
		// A documentation site is a legitimate project URL. Only a URL claiming to be this repository
		// has to spell this repository correctly.
		var result = await Evaluate(Csproj(
			repositoryUrl: "https://github.com/panoramicdata/Dell.CloudIq.Api",
			packageProjectUrl: "https://panoramicdata.com/products/cloudiq"));

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task Passes_WhenPackageProjectUrlNamesAnUnrelatedGitHubRepository()
	{
		var result = await Evaluate(Csproj(
			repositoryUrl: "https://github.com/panoramicdata/Dell.CloudIq.Api",
			packageProjectUrl: "https://github.com/dotnet/runtime"));

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task Passes_WhenATrailingGitSuffixIsTheOnlyDifference()
	{
		var result = await Evaluate(Csproj(
			repositoryUrl: "https://github.com/panoramicdata/Dell.CloudIq.Api.git",
			packageProjectUrl: null));

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task Passes_WhenNoUrlIsDeclaredAtAll()
	{
		// Absent is META-02's and META-04's finding, not this rule's.
		var result = await Evaluate(Csproj(repositoryUrl: null, packageProjectUrl: null));

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task Passes_WhenTheRepositoryIsNotHostedOnGitHub()
	{
		var result = await Evaluate(Csproj(
			repositoryUrl: "https://dev.azure.com/panoramicdata/_git/Dell.CloudIq.Api",
			packageProjectUrl: null));

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task NamesEveryOffendingFile_WhenTwoProjectsDisagree()
	{
		var files = new Dictionary<string, string>
		{
			["First/First.csproj"] = Project("https://github.com/panoramicdata/Dell.CloudIQ.Api", null),
			["Second/Second.csproj"] = Project("https://github.com/panoramicdata/dell.cloudiq.api", null)
		};

		var result = await Evaluate(files);

		result.Passed.Should().BeFalse();
		var globs = (string[])result.Advisory!.Data["globs"];
		globs.Should().BeEquivalentTo(["First/First.csproj", "Second/Second.csproj"]);
		((string[])result.Advisory.Data["patterns"]).Should().HaveCount(2);
	}

	private static Task<RuleResult> Evaluate(Dictionary<string, string> files)
		=> new DeclaredRepositoryUrlRule().EvaluateAsync(
			Context(files),
			TestContext.Current.CancellationToken);

	private static Dictionary<string, string> Csproj(string? repositoryUrl, string? packageProjectUrl)
		=> new() { ["Dell.CloudIq.Api/Dell.CloudIq.Api.csproj"] = Project(repositoryUrl, packageProjectUrl) };

	private static string Project(string? repositoryUrl, string? packageProjectUrl)
		=> $"""
			<Project Sdk="Microsoft.NET.Sdk">
			  <PropertyGroup>
			    <IsPackable>true</IsPackable>
			    <PackageId>Dell.CloudIq.Api</PackageId>
			    {(repositoryUrl is null ? "" : $"<RepositoryUrl>{repositoryUrl}</RepositoryUrl>")}
			    {(packageProjectUrl is null ? "" : $"<PackageProjectUrl>{packageProjectUrl}</PackageProjectUrl>")}
			  </PropertyGroup>
			</Project>
			""";

	private static RepositoryContext Context(Dictionary<string, string> files)
		=> new()
		{
			FullName = "panoramicdata/Dell.CloudIq.Api",
			Name = "Dell.CloudIq.Api",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = [.. files.Keys],
			FileContents = files
		};
}
