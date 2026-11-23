using System.Text;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests LIC-05: no project file overrides the shared Copyright, and the fix that removes it.
/// </summary>
public class CopyrightOverrideTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _overridingProject = "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\t<PropertyGroup>\r\n\t\t<TargetFramework>net10.0</TargetFramework>\r\n\t\t<Copyright>Panoramic Data Limited 2025</Copyright>\r\n\t</PropertyGroup>\r\n</Project>\r\n";
	private const string _cleanProject = "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\t<PropertyGroup>\r\n\t\t<TargetFramework>net10.0</TargetFramework>\r\n\t</PropertyGroup>\r\n</Project>\r\n";

	[Fact]
	public async Task Rule_ShouldFail_WhenAProjectSetsCopyright()
	{
		var result = await Evaluate(new()
		{
			["Acme.Lib/Acme.Lib.csproj"] = _overridingProject,
			["Acme.Lib.Test/Acme.Lib.Test.csproj"] = _cleanProject
		});

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("Acme.Lib/Acme.Lib.csproj");
		result.Advisory!.Data["remediation_type"].Should().Be("remove_xml_property");
		result.Advisory.Data["property_name"].Should().Be("Copyright");
		(result.Advisory.Data["projects"] as string[]).Should().Equal("Acme.Lib/Acme.Lib.csproj");
	}

	[Fact]
	public async Task Rule_ShouldPass_WhenNoProjectSetsCopyright()
	{
		var result = await Evaluate(new() { ["Acme.Lib/Acme.Lib.csproj"] = _cleanProject });

		result.Passed.Should().BeTrue(result.Message);
		result.IsApplicable.Should().BeTrue();
	}

	[Fact]
	public async Task Rule_ShouldNotFireOnDirectoryBuildProps()
	{
		var result = await Evaluate(new()
		{
			["Directory.Build.props"] = "<Project><PropertyGroup><Copyright>Copyright © Panoramic Data Limited</Copyright></PropertyGroup></Project>",
			["Acme.Lib/Acme.Lib.csproj"] = _cleanProject
		});

		result.Passed.Should().BeTrue(result.Message);
	}

	[Fact]
	public async Task Rule_ShouldIgnoreACommentedOutCopyright()
	{
		var result = await Evaluate(new()
		{
			["Acme.Lib/Acme.Lib.csproj"] = "<Project><PropertyGroup><!-- <Copyright>old</Copyright> --></PropertyGroup></Project>"
		});

		result.Passed.Should().BeTrue(result.Message);
	}

	[Fact]
	public async Task Rule_ShouldBeNotApplicable_WhenThereAreNoProjects()
	{
		var result = await Evaluate(new() { ["Directory.Build.props"] = "<Project />" });

		result.IsApplicable.Should().BeFalse();
	}

	[Fact]
	public void Rule_IsRegisteredInLicensing()
	{
		var rule = RuleRegistry.Rules.Single(r => r.RuleId == "LIC-05");

		rule.Category.Should().Be(AssessmentCategory.Licensing);
		rule.Severity.Should().Be(AssessmentSeverity.Error);
	}

	[Fact]
	public async Task Remediation_RemovesTheElementKeepingLineEndings_ThenTheRulePasses()
	{
		var root = NewRoot();
		try
		{
			var path = Path.Combine(root, "Acme.Lib.csproj");
			File.WriteAllBytes(path, Encoding.UTF8.GetBytes(_overridingProject));

			var failed = await Evaluate(new() { ["Acme.Lib.csproj"] = _overridingProject });
			var registry = new RemediationRegistry();
			registry.CanRemediate(failed).Should().BeTrue();

			var applied = new List<string>();
			registry.Get("LIC-05")!.Apply(root, failed, applied, null);

			applied.Should().Equal("Acme.Lib.csproj");
			File.ReadAllBytes(path).Should().Equal(Encoding.UTF8.GetBytes(_cleanProject));
			(await Evaluate(new() { ["Acme.Lib.csproj"] = _cleanProject })).Passed.Should().BeTrue();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public async Task Remediation_KeepsTheBom()
	{
		var root = NewRoot();
		try
		{
			var path = Path.Combine(root, "Acme.Lib.csproj");
			var bom = new UTF8Encoding(true);
			File.WriteAllBytes(path, [.. bom.GetPreamble(), .. bom.GetBytes(_overridingProject)]);

			var failed = await Evaluate(new() { ["Acme.Lib.csproj"] = _overridingProject });
			new RemediationRegistry().Get("LIC-05")!.Apply(root, failed, [], null);

			File.ReadAllBytes(path).Should().Equal([.. bom.GetPreamble(), .. bom.GetBytes(_cleanProject)]);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public async Task Remediation_RefusesAPathOutsideTheRepository()
	{
		var root = NewRoot();
		var outside = Path.Combine(Path.GetTempPath(), "lic05-outside-" + Guid.NewGuid().ToString("N") + ".csproj");
		try
		{
			File.WriteAllText(outside, _overridingProject);
			var failed = await Evaluate(new() { ["Acme.Lib.csproj"] = _overridingProject });
			failed.Advisory!.Data["projects"] = new[] { Path.Combine("..", Path.GetFileName(outside)) };

			var applied = new List<string>();
			new RemediationRegistry().Get("LIC-05")!.Apply(root, failed, applied, null);

			applied.Should().BeEmpty();
			File.ReadAllText(outside).Should().Contain("<Copyright>");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
			File.Delete(outside);
		}
	}

	[Fact]
	public void Remediation_CannotRemediate_WithoutTheDataItReads()
	{
		var bare = new RuleResult
		{
			RuleId = "LIC-05",
			RuleName = "x",
			Category = AssessmentCategory.Licensing,
			Severity = AssessmentSeverity.Error,
			Passed = false,
			Message = "x",
			Advisory = new RuleAdvisory { Summary = "x", Detail = "x", Data = new() { ["remediation_type"] = "remove_xml_property" } }
		};

		new RemediationRegistry().CanRemediate(bare).Should().BeFalse();
	}

	private static string NewRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "lic05-" + Guid.NewGuid().ToString("N"));
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
