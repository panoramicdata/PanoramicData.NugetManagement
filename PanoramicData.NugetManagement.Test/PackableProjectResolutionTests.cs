using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for deciding which projects a repository actually publishes. Resolution is by evidence and
/// by explicit nomination — never by whether a .csproj happens to be named after the repository,
/// which silently skipped every packaging rule for around a quarter of the estate.
/// </summary>
public class PackableProjectResolutionTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _packageId = "<Project><PropertyGroup><PackageId>Acme.Widget</PackageId></PropertyGroup></Project>";
	private const string _generateOnBuild = "<Project><PropertyGroup><GeneratePackageOnBuild>true</GeneratePackageOnBuild></PropertyGroup></Project>";
	private const string _packAsTool = "<Project><PropertyGroup><PackAsTool>true</PackAsTool></PropertyGroup></Project>";
	private const string _isPackableTrue = "<Project><PropertyGroup><IsPackable>true</IsPackable></PropertyGroup></Project>";
	private const string _isPackableFalse = "<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>";
	private const string _silent = "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";

	[Theory]
	[InlineData(_packageId)]
	[InlineData(_generateOnBuild)]
	[InlineData(_packAsTool)]
	[InlineData(_isPackableTrue)]
	public void FindPackableProjectFiles_ShouldAcceptAnyPackingEvidence(string csproj)
	{
		var context = CreateContext("Acme.Widget", new() { ["Src/Anything.csproj"] = csproj });

		context.FindPackableProjectFiles().Should().ContainSingle().Which.Should().Be("Src/Anything.csproj");
	}

	[Fact]
	public void FindPackableProjectFiles_ShouldIgnoreAProjectWithNoEvidence()
	{
		var context = CreateContext("Acme.Widget", new() { ["Src/Anything.csproj"] = _silent });

		context.FindPackableProjectFiles().Should().BeEmpty();
	}

	[Fact]
	public void FindPackableProjectFiles_ShouldNotResolveByProjectName()
	{
		var context = CreateContext("Acme.Widget", new() { ["Acme.Widget/Acme.Widget.csproj"] = _silent });

		context.FindPackableProjectFiles().Should().BeEmpty(
			"matching the repository name is what hid unassessed packages in the first place");
	}

	[Fact]
	public void FindPackableProjectFiles_ShouldFindTheProjectWhateverItIsCalled()
	{
		// The ConnectWise.Api case: the package project is not named after the repository.
		var context = CreateContext("ConnectWise.Api", new()
		{
			["ConnectWise.Api/ConnectWise.Manage.Api.csproj"] = _packageId
		});

		context.FindPackableProjectFiles().Should().ContainSingle()
			.Which.Should().Be("ConnectWise.Api/ConnectWise.Manage.Api.csproj");
	}

	[Fact]
	public void FindPackableProjectFiles_ShouldExcludeAProjectThatOptsOut()
	{
		// IsPackable=false wins over other evidence: a project that says it is not published is not.
		var context = CreateContext("Acme.Widget", new()
		{
			["Src/Widget.csproj"] = "<Project><PropertyGroup><PackageId>Acme.Widget</PackageId><IsPackable>false</IsPackable></PropertyGroup></Project>"
		});

		context.FindPackableProjectFiles().Should().BeEmpty();
	}

	[Fact]
	public void FindPackableProjectFiles_ShouldExcludeTestProjects()
	{
		var context = CreateContext("Acme.Widget", new()
		{
			["Src/Widget.csproj"] = _packageId,
			["Src/Widget.Test/Widget.Test.csproj"] = _packageId
		});

		context.FindPackableProjectFiles().Should().ContainSingle().Which.Should().Be("Src/Widget.csproj");
	}

	[Fact]
	public void FindPackableProjectFiles_ShouldReturnEveryPackageInAMultiPackageRepository()
	{
		// The PanoramicData.HealthChecks case: several published packages, none named after the repo.
		var context = CreateContext("PanoramicData.HealthChecks", new()
		{
			["PanoramicData.HealthChecks.Core/PanoramicData.HealthChecks.Core.csproj"] = _generateOnBuild,
			["PanoramicData.HealthChecks.BasicAuthentication/PanoramicData.HealthChecks.BasicAuthentication.csproj"] = _generateOnBuild,
			["PanoramicData.HealthChecks.Sample/PanoramicData.HealthChecks.Sample.csproj"] = _isPackableFalse
		});

		context.FindPackableProjectFiles().Should().HaveCount(2);
	}

	[Fact]
	public void FindPackableProjectFiles_ShouldHonourAConfigNomination()
	{
		// Nomination is for the repository that publishes a project the evidence cannot identify.
		var config = new NugetManagementRepositoryConfig
		{
			Projects = new()
			{
				["Src/Widget.csproj"] = new() { PackagingTreatment = ProjectTreatment.Include }
			}
		};

		var context = CreateContext("Acme.Widget", new() { ["Src/Widget.csproj"] = _silent }, config);

		context.FindPackableProjectFiles().Should().ContainSingle().Which.Should().Be("Src/Widget.csproj");
	}

	[Fact]
	public void FindPackableProjectFiles_ShouldHonourAConfigExclusion()
	{
		var config = new NugetManagementRepositoryConfig
		{
			Projects = new()
			{
				["Src/Widget.csproj"] = new() { PackagingTreatment = ProjectTreatment.Exclude }
			}
		};

		var context = CreateContext("Acme.Widget", new() { ["Src/Widget.csproj"] = _packageId }, config);

		context.FindPackableProjectFiles().Should().BeEmpty("the repository has said this one is not published");
	}

	[Fact]
	public void FindNonPackableProjectFiles_ShouldReturnWhatIsLeft()
	{
		var context = CreateContext("Acme.Widget", new()
		{
			["Src/Widget.csproj"] = _packageId,
			["Tools/Importer.csproj"] = _silent,
			["Src/Widget.Test/Widget.Test.csproj"] = _silent
		});

		context.FindNonPackableProjectFiles().Should().ContainSingle()
			.Which.Should().Be("Tools/Importer.csproj", "test projects are not ancillary, they are tests");
	}

	[Fact]
	public void FindNonPackableProjectFiles_ShouldNotClaimThePackageItselfWhenNothingIsNamedAfterTheRepo()
	{
		// The bug the other way round: with no name match, every project counted as ancillary and
		// PKG-09 demanded IsPackable=false on the repository's actual package.
		var context = CreateContext("ConnectWise.Api", new()
		{
			["ConnectWise.Api/ConnectWise.Manage.Api.csproj"] = _generateOnBuild
		});

		context.FindNonPackableProjectFiles().Should().BeEmpty();
	}

	private static RepositoryContext CreateContext(
		string name,
		Dictionary<string, string> files,
		NugetManagementRepositoryConfig? config = null) => new()
		{
			FullName = $"test-org/{name}",
			Name = name,
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = [.. files.Keys],
			FileContents = files,
			RepositoryConfig = config
		};
}
