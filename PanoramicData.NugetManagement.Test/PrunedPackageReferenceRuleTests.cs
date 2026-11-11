using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for PKG-15: a package the net11.0 framework supplies is pruned by NuGet, and NU1510 turns the
/// leftover reference into a build error that blocks the 10 to 11 upgrade from being pushed.
/// </summary>
public class PrunedPackageReferenceRuleTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private const string _csprojPath = "Widget/Widget.csproj";
	private const string _propsPath = "Directory.Packages.props";

	private readonly string _root = Directory.CreateTempSubdirectory("nugetmgmt-pkg15-").FullName;

	private static string Project(string framework, string references)
		=> $"<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\t<PropertyGroup>\r\n\t\t<TargetFramework>{framework}</TargetFramework>\r\n\t</PropertyGroup>\r\n\t<ItemGroup>\r\n{references}\t</ItemGroup>\r\n</Project>\r\n";

	private static RepositoryContext Ctx(Dictionary<string, string> files) => new()
	{
		FullName = "panoramicdata/Sample",
		Name = "Sample",
		DefaultBranch = "main",
		CurrentBranch = "main",
		Options = new RepoOptions(),
		FilePaths = [.. files.Keys],
		FileContents = files
	};

	private static Task<RuleResult> EvaluateAsync(Dictionary<string, string> files)
		=> RuleRegistry.Rules.Single(r => r.RuleId == "PKG-15").EvaluateAsync(Ctx(files), TestContext.Current.CancellationToken);

	[Fact]
	public async Task Fails_ForAnInlineVersionTheFrameworkSupplies()
	{
		var result = await EvaluateAsync(new()
		{
			[_csprojPath] = Project("net11.0", "\t\t<PackageReference Include=\"Microsoft.Extensions.Logging.Abstractions\" Version=\"11.0.0\" />\r\n")
		});

		result.Passed.Should().BeFalse();
		result.Advisory!.Data["removals"].Should().BeEquivalentTo(new[] { $"{_csprojPath}|Microsoft.Extensions.Logging.Abstractions" });
	}

	[Fact]
	public async Task Fails_ForACentrallyManagedVersionTheFrameworkSupplies()
	{
		var result = await EvaluateAsync(new()
		{
			[_csprojPath] = Project("net11.0", "\t\t<PackageReference Include=\"Microsoft.Extensions.Options\" />\r\n"),
			[_propsPath] = "<Project><ItemGroup><PackageVersion Include=\"Microsoft.Extensions.Options\" Version=\"10.0.14\" /></ItemGroup></Project>"
		});

		result.Passed.Should().BeFalse("10.0.14 is below the 11.0.0 the framework carries, so it is pruned");
	}

	[Fact]
	public async Task Passes_WhenTheReferenceIsNewerThanTheFramework()
	{
		var result = await EvaluateAsync(new()
		{
			[_csprojPath] = Project("net11.0", "\t\t<PackageReference Include=\"Microsoft.Extensions.Logging.Abstractions\" Version=\"11.0.1\" />\r\n")
		});

		result.Passed.Should().BeTrue("a newer version is not pruned and is the project's own choice");
	}

	[Fact]
	public async Task Passes_ForAProjectNotYetOnNet11()
	{
		var result = await EvaluateAsync(new()
		{
			[_csprojPath] = Project("net10.0", "\t\t<PackageReference Include=\"Microsoft.Extensions.Logging.Abstractions\" Version=\"10.0.14\" />\r\n")
		});

		result.Passed.Should().BeTrue("net10.0 does not carry that package, so it is still needed there");
	}

	[Fact]
	public async Task Passes_ForAPackageTheFrameworkDoesNotSupply()
	{
		var result = await EvaluateAsync(new()
		{
			[_csprojPath] = Project("net11.0", "\t\t<PackageReference Include=\"Refit\" Version=\"16.4.0\" />\r\n")
		});

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task Fix_RemovesOnlyThePrunedReferenceAndTheProjectThenPasses()
	{
		var csproj = Project(
			"net11.0",
			"\t\t<PackageReference Include=\"Microsoft.Extensions.Logging.Abstractions\" Version=\"11.0.0\" />\r\n"
			+ "\t\t<PackageReference Include=\"Refit\" Version=\"16.4.0\" />\r\n");
		var files = new Dictionary<string, string> { [_csprojPath] = csproj };
		Directory.CreateDirectory(Path.Combine(_root, "Widget"));
		File.WriteAllText(Path.Combine(_root, _csprojPath), csproj);

		var result = await EvaluateAsync(files);
		var registry = new RemediationRegistry();
		registry.CanRemediate(result).Should().BeTrue("PKG-15 must be fixable without the AI");

		var applied = new List<string>();
		registry.Get("PKG-15")!.Apply(_root, result, applied, Output.WriteLine);
		applied.Should().NotBeEmpty();

		var fixedProject = File.ReadAllText(Path.Combine(_root, _csprojPath));
		fixedProject.Should().NotContain("Logging.Abstractions");
		fixedProject.Should().Contain("Refit", "a package the framework does not supply must stay");

		var reassessed = await EvaluateAsync(new() { [_csprojPath] = fixedProject });
		reassessed.Passed.Should().BeTrue();
	}

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// A locked temp file must not fail the test that produced it.
		}

		GC.SuppressFinalize(this);
	}
}
