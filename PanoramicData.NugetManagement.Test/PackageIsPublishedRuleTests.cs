using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// PKG-14: a repository that declares a package nuget.org has never been given is wired to publish
/// and has never shipped — typically a tag that never fired, an identifier nuget.org refuses, or a
/// green publish run that pushed nothing because <c>--skip-duplicate</c> hid a Conflict.
/// </summary>
public class PackageIsPublishedRuleTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _publishedPackage =
		"<Project><PropertyGroup><PackageId>Acme.Lib</PackageId></PropertyGroup></Project>";

	[Fact]
	public async Task PKG14_ShouldFail_WhenTheDeclaredPackageIsNotOnNuGet()
	{
		var rule = new PackageIsPublishedRule((_, _) => Task.FromResult<bool?>(false));

		var result = await rule.EvaluateAsync(CreateContext(), CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Warning);
		result.Message.Should().Contain("Acme.Lib");
		result.Advisory.Should().NotBeNull();
		result.Advisory!.Summary.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public async Task PKG14_ShouldPass_WhenThePackageIsPublished()
	{
		var rule = new PackageIsPublishedRule((_, _) => Task.FromResult<bool?>(true));

		var result = await rule.EvaluateAsync(CreateContext(), CancellationToken.None);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task PKG14_ShouldPass_WhenNuGetCannotBeReached()
	{
		var rule = new PackageIsPublishedRule((_, _) => Task.FromResult<bool?>(null));

		var result = await rule.EvaluateAsync(CreateContext(), CancellationToken.None);

		result.Passed.Should().BeTrue("an unknown answer must never be reported as a repository defect");
	}

	[Fact]
	public async Task PKG14_ShouldAskAboutTheDeclaredPackageId_NotTheProjectFileName()
	{
		var queried = new List<string>();
		var rule = new PackageIsPublishedRule((packageId, _) =>
		{
			queried.Add(packageId);
			return Task.FromResult<bool?>(true);
		});

		await rule.EvaluateAsync(CreateContext(), CancellationToken.None);

		queried.Should().Equal("Acme.Lib");
	}

	[Fact]
	public async Task PKG14_ShouldOnlyReportThePackagesThatAreMissing()
	{
		var rule = new PackageIsPublishedRule((packageId, _) => Task.FromResult<bool?>(packageId != "Acme.Suite.Missing"));

		var context = CreateContext(new Dictionary<string, string>
		{
			["Acme.Suite/Acme.Suite.csproj"] =
				"<Project><PropertyGroup><PackageId>Acme.Suite</PackageId></PropertyGroup></Project>",
			["Acme.Suite.Missing/Acme.Suite.Missing.csproj"] =
				"<Project><PropertyGroup><PackageId>Acme.Suite.Missing</PackageId></PropertyGroup></Project>"
		});

		var result = await rule.EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("Acme.Suite.Missing");
		result.Message.Should().NotContain("Acme.Suite,");
	}

	[Fact]
	public async Task PKG14_ShouldNotApply_WhenNoProjectIsPackable()
	{
		var rule = new PackageIsPublishedRule((_, _) => Task.FromResult<bool?>(false));

		var context = CreateContext(new Dictionary<string, string>
		{
			["Acme.Lib/Acme.Lib.csproj"] = "<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>"
		});

		var result = await rule.EvaluateAsync(context, CancellationToken.None);

		result.IsApplicable.Should().BeFalse();
	}

	[Fact]
	public async Task PKG14_ShouldNotRequestAnAutomatedRemediation()
	{
		var rule = new PackageIsPublishedRule((_, _) => Task.FromResult<bool?>(false));

		var result = await rule.EvaluateAsync(CreateContext(), CancellationToken.None);

		result.Advisory!.Data.Should().NotContainKey(
			"remediation_type",
			"releasing to nuget.org cannot be undone, so it must never be applied unattended");
	}

	private static RepositoryContext CreateContext(Dictionary<string, string>? files = null)
	{
		files ??= new Dictionary<string, string> { ["Acme.Lib/Acme.Lib.csproj"] = _publishedPackage };

		return new()
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
}
