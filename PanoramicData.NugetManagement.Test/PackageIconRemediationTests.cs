using System.Text;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Remediations.ProjectMetadata;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="PackageIconRemediation"/>, which replaced a model-driven fix after the model
/// wrote the base64 text of a PNG into a file named Logo.png.
/// </summary>
public class PackageIconRemediationTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		$"icon-{Guid.NewGuid():n}");

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private static RuleResult Failing(params string[] projects) => new()
	{
		RuleId = "META-05",
		RuleName = "PackageIcon set",
		Category = AssessmentCategory.ProjectMetadata,
		Severity = AssessmentSeverity.Info,
		Passed = false,
		Message = "no icon",
		Advisory = new RuleAdvisory
		{
			Summary = "x",
			Detail = "x",
			Data = new() { ["projects"] = projects }
		}
	};

	private string WriteProject(string name, string text, bool bom = false, bool crlf = false)
	{
		Directory.CreateDirectory(Path.Combine(_root, Path.GetDirectoryName(name)!));
		var path = Path.Combine(_root, name);

		if (crlf)
		{
			text = text.Replace("\n", "\r\n", StringComparison.Ordinal);
		}

		File.WriteAllText(path, text, new UTF8Encoding(bom));

		return path;
	}

	private const string Plain = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <PackageId>X</PackageId>\n  </PropertyGroup>\n</Project>\n";

	[Fact]
	public void Apply_AddsThePropertyTheItemAndARealImage()
	{
		var project = WriteProject("src/X.csproj", Plain);
		var applied = new List<string>();

		new PackageIconRemediation().Apply(_root, Failing("src/X.csproj"), applied, null);

		var text = File.ReadAllText(project);
		text.Should().Contain("<PackageIcon>Logo.png</PackageIcon>");
		text.Should().Contain("<None Include=\"Logo.png\" Pack=\"true\" PackagePath=\"\\\" />");

		var logo = File.ReadAllBytes(Path.Combine(_root, "src", "Logo.png"));
		logo.Take(8).Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
		applied.Should().Contain(["src/Logo.png", "src/X.csproj"]);
	}

	/// <summary>
	/// The edit has to read like a person made it: new lines, indented like their neighbours, and nothing
	/// else in the file moved. An XML round trip once squashed both onto existing lines.
	/// </summary>
	[Fact]
	public void Apply_InsertsWholeIndentedLines_AndChangesNothingElse()
	{
		var project = WriteProject("X.csproj", Plain);

		new PackageIconRemediation().Apply(_root, Failing("X.csproj"), [], null);

		File.ReadAllText(project).Should().Be(
			"<Project Sdk=\"Microsoft.NET.Sdk\">\n"
			+ "  <PropertyGroup>\n"
			+ "    <PackageId>X</PackageId>\n"
			+ "    <PackageIcon>Logo.png</PackageIcon>\n"
			+ "  </PropertyGroup>\n"
			+ "  <ItemGroup>\n"
			+ "    <None Include=\"Logo.png\" Pack=\"true\" PackagePath=\"\\\" />\n"
			+ "  </ItemGroup>\n"
			+ "</Project>\n");
	}

	[Fact]
	public void Apply_IsIdempotent()
	{
		var project = WriteProject("X.csproj", Plain);
		var remediation = new PackageIconRemediation();

		remediation.Apply(_root, Failing("X.csproj"), [], null);
		var once = File.ReadAllText(project);

		var second = new List<string>();
		remediation.Apply(_root, Failing("X.csproj"), second, null);

		File.ReadAllText(project).Should().Be(once);
		second.Should().BeEmpty();
	}

	[Fact]
	public void Apply_KeepsAnExistingImage()
	{
		WriteProject("X.csproj", Plain);
		File.WriteAllBytes(Path.Combine(_root, "Logo.png"), [1, 2, 3]);

		new PackageIconRemediation().Apply(_root, Failing("X.csproj"), [], null);

		File.ReadAllBytes(Path.Combine(_root, "Logo.png")).Should().Equal(1, 2, 3);
	}

	[Fact]
	public void Apply_KeepsTheByteOrderMarkAndLineEndings()
	{
		var project = WriteProject("X.csproj", Plain, bom: true, crlf: true);

		new PackageIconRemediation().Apply(_root, Failing("X.csproj"), [], null);

		var bytes = File.ReadAllBytes(project);
		bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);

		var text = Encoding.UTF8.GetString(bytes);
		text.Should().Contain("\r\n");
		text.Replace("\r\n", string.Empty, StringComparison.Ordinal).Should().NotContain("\n");
	}

	[Fact]
	public void Apply_ToAProjectThatDoesNotExist_DoesNothing()
	{
		var applied = new List<string>();

		new PackageIconRemediation().Apply(_root, Failing("missing.csproj"), applied, null);

		applied.Should().BeEmpty();
	}

	[Fact]
	public void CanRemediate_NeedsTheProjectsTheRuleNamed()
	{
		new PackageIconRemediation().CanRemediate(Failing("X.csproj")).Should().BeTrue();

		new PackageIconRemediation().CanRemediate(new RuleResult
		{
			RuleId = "META-05",
			RuleName = "x",
			Category = AssessmentCategory.ProjectMetadata,
			Severity = AssessmentSeverity.Info,
			Passed = true,
			Message = "ok"
		}).Should().BeFalse();
	}
}
