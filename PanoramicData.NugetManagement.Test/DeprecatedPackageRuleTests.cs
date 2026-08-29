using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the two deprecation rules: PKG-11, which catches a repository still being governed
/// after its own package was deprecated (it should have been archived), and PKG-12, which catches a
/// repository depending on someone else's deprecated package.
/// </summary>
public class DeprecatedPackageRuleTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _publishedPackage =
		"<Project><PropertyGroup><PackageId>Acme.Lib</PackageId></PropertyGroup></Project>";

	[Fact]
	public async Task PKG11_ShouldFail_WhenThePublishedPackageIsDeprecated()
	{
		var rule = new DeprecatedPackageRepositoryArchivedRule(
			(packageId, _, _) => Task.FromResult<PackageDeprecationStatus?>(
				new PackageDeprecationStatus(packageId, ["Legacy"], "Use Acme.Lib2 instead.", "Acme.Lib2")));

		var context = CreateContext("Acme.Lib", new Dictionary<string, string>
		{
			["Acme.Lib/Acme.Lib.csproj"] = _publishedPackage
		});

		var result = await rule.EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Error);
		result.Message.Should().Contain("Acme.Lib");
	}

	[Fact]
	public async Task PKG11_ShouldPass_WhenThePublishedPackageIsNotDeprecated()
	{
		var rule = new DeprecatedPackageRepositoryArchivedRule(NotDeprecated);

		var context = CreateContext("Acme.Lib", new Dictionary<string, string>
		{
			["Acme.Lib/Acme.Lib.csproj"] = _publishedPackage
		});

		var result = await rule.EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task PKG11_ShouldCheckTheDeclaredPackageId_NotTheProjectFileName()
	{
		var queried = new List<string>();
		var rule = new DeprecatedPackageRepositoryArchivedRule((packageId, _, _) =>
		{
			queried.Add(packageId);
			return Task.FromResult<PackageDeprecationStatus?>(null);
		});

		var context = CreateContext("ConnectWise.Api", new Dictionary<string, string>
		{
			["Src/ConnectWise.Manage.Api.csproj"] =
				"<Project><PropertyGroup><PackageId>ConnectWise.Api</PackageId></PropertyGroup></Project>"
		});

		await rule.EvaluateAsync(context, CancellationToken.None);

		queried.Should().ContainSingle().Which.Should().Be("ConnectWise.Api");
	}

	[Fact]
	public async Task PKG11_ShouldFallBackToTheProjectFileName_WhenNoPackageIdIsDeclared()
	{
		var queried = new List<string>();
		var rule = new DeprecatedPackageRepositoryArchivedRule((packageId, _, _) =>
		{
			queried.Add(packageId);
			return Task.FromResult<PackageDeprecationStatus?>(null);
		});

		var context = CreateContext("Acme.Widget", new Dictionary<string, string>
		{
			["Src/Acme.Widget.csproj"] =
				"<Project><PropertyGroup><GeneratePackageOnBuild>true</GeneratePackageOnBuild></PropertyGroup></Project>"
		});

		await rule.EvaluateAsync(context, CancellationToken.None);

		queried.Should().ContainSingle().Which.Should().Be("Acme.Widget");
	}

	[Fact]
	public async Task PKG11_ShouldNotQueryNuGet_WhenTheRepositoryPublishesNothing()
	{
		var queried = new List<string>();
		var rule = new DeprecatedPackageRepositoryArchivedRule((packageId, _, _) =>
		{
			queried.Add(packageId);
			return Task.FromResult<PackageDeprecationStatus?>(null);
		});

		var context = CreateContext(
			"Acme.Service",
			new Dictionary<string, string>
			{
				["Src/Acme.Service.csproj"] = "<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>"
			},
			new RepoOptions { IsPackable = false });

		var result = await rule.EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue();
		queried.Should().BeEmpty("a repository that publishes nothing has no package to be deprecated");
	}

	[Fact]
	public async Task PKG11_ShouldNameTheAlternativeAndReasonInTheAdvisory()
	{
		var rule = new DeprecatedPackageRepositoryArchivedRule(
			(packageId, _, _) => Task.FromResult<PackageDeprecationStatus?>(
				new PackageDeprecationStatus(packageId, ["Legacy", "Other"], "Uses unsupported scraping.", "Acme.Lib2")));

		var context = CreateContext("Acme.Lib", new Dictionary<string, string>
		{
			["Acme.Lib/Acme.Lib.csproj"] = _publishedPackage
		});

		var result = await rule.EvaluateAsync(context, CancellationToken.None);

		result.Advisory!.Detail.Should().Contain("Legacy").And.Contain("Acme.Lib2");
		result.Advisory.Data["deprecated_packages"].Should().BeEquivalentTo(new[] { "Acme.Lib" });
		result.Advisory.Data.Should().NotContainKey(
			"remediation_type",
			"archiving a repository is outward-facing and irreversible-feeling, so it must never be applied unattended");
	}

	[Fact]
	public async Task PKG12_ShouldFail_WhenAReferencedPackageIsDeprecated()
	{
		var rule = new NoDeprecatedDependenciesRule(DeprecatesFxCop);

		var context = CreateContext("Acme.Lib", new Dictionary<string, string>
		{
			["Acme.Lib/Acme.Lib.csproj"] = """
				<Project><ItemGroup>
					<PackageReference Include="Microsoft.CodeAnalysis.FxCopAnalyzers" Version="3.3.2" />
					<PackageReference Include="Refit" Version="8.0.0" />
				</ItemGroup></Project>
				"""
		});

		var result = await rule.EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("Microsoft.CodeAnalysis.FxCopAnalyzers");
		result.Message.Should().Contain("Microsoft.CodeAnalysis.NetAnalyzers");
		result.Message.Should().NotContain("Refit", "a package that is not deprecated must not be reported");
	}

	[Fact]
	public async Task PKG12_ShouldPass_WhenNoReferencedPackageIsDeprecated()
	{
		var rule = new NoDeprecatedDependenciesRule(NotDeprecated);

		var context = CreateContext("Acme.Lib", new Dictionary<string, string>
		{
			["Acme.Lib/Acme.Lib.csproj"] =
				"""<Project><ItemGroup><PackageReference Include="Refit" Version="8.0.0" /></ItemGroup></Project>"""
		});

		var result = await rule.EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task PKG12_ShouldFindPackagesPinnedByCentralPackageManagement()
	{
		var rule = new NoDeprecatedDependenciesRule(DeprecatesFxCop);

		var context = CreateContext("Acme.Lib", new Dictionary<string, string>
		{
			["Directory.Packages.props"] = """
				<Project><ItemGroup>
					<PackageVersion Include="Microsoft.CodeAnalysis.FxCopAnalyzers" Version="3.3.2" />
				</ItemGroup></Project>
				"""
		});

		var result = await rule.EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("Directory.Packages.props");
	}

	[Fact]
	public async Task PKG12_ShouldNotRequestAnAutomatedRemediation()
	{
		var rule = new NoDeprecatedDependenciesRule(DeprecatesFxCop);

		var context = CreateContext("Acme.Lib", new Dictionary<string, string>
		{
			["Acme.Lib/Acme.Lib.csproj"] =
				"""<Project><ItemGroup><PackageReference Include="Microsoft.CodeAnalysis.FxCopAnalyzers" Version="3.3.2" /></ItemGroup></Project>"""
		});

		var result = await rule.EvaluateAsync(context, CancellationToken.None);

		result.Advisory!.Data.Should().NotContainKey(
			"remediation_type",
			"swapping to a different package is a code change that must not be applied unattended");
	}

	[Fact]
	public async Task PKG12_ShouldQueryEachPackageVersionOnlyOnce()
	{
		var queries = new List<string>();
		var rule = new NoDeprecatedDependenciesRule((packageId, version, _) =>
		{
			queries.Add($"{packageId}/{version}");
			return Task.FromResult<PackageDeprecationStatus?>(null);
		});

		var context = CreateContext("Acme.Suite", new Dictionary<string, string>
		{
			["A/A.csproj"] = """<Project><ItemGroup><PackageReference Include="Refit" Version="8.0.0" /></ItemGroup></Project>""",
			["B/B.csproj"] = """<Project><ItemGroup><PackageReference Include="Refit" Version="8.0.0" /></ItemGroup></Project>"""
		});

		await rule.EvaluateAsync(context, CancellationToken.None);

		queries.Should().ContainSingle("the same package version referenced twice is one question for nuget.org");
	}

	private static Task<PackageDeprecationStatus?> DeprecatesFxCop(string packageId, string? version, CancellationToken cancellationToken)
		=> Task.FromResult(packageId == "Microsoft.CodeAnalysis.FxCopAnalyzers"
			? new PackageDeprecationStatus(
				packageId,
				["Legacy"],
				"Use Microsoft.CodeAnalysis.NetAnalyzers instead.",
				"Microsoft.CodeAnalysis.NetAnalyzers")
			: null);

	private static Task<PackageDeprecationStatus?> NotDeprecated(string packageId, string? version, CancellationToken cancellationToken)
		=> Task.FromResult<PackageDeprecationStatus?>(null);

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
