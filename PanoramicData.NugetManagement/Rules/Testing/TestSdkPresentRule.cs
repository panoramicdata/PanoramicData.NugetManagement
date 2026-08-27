using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that Microsoft.NET.Test.Sdk is referenced.
/// </summary>
public class TestSdkPresentRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "TST-03";

	/// <inheritdoc />
	public override string RuleName => "Microsoft.NET.Test.Sdk referenced";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Testing;

	// Warning, not Error: under Microsoft.Testing.Platform xunit.v3 supplies its own entry point, so
	// Microsoft.NET.Test.Sdk is no longer what makes tests runnable. It remains worth having — it is
	// harmless and still sets IsTestProject — but its absence is not a hard failure until someone
	// confirms a repository cannot run its tests without it.
	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var dirPackages = context.GetFileContent("Directory.Packages.props");
		if (ReferencesPackage(dirPackages, "Microsoft.NET.Test.Sdk", includeVariants: true))
		{
			return Task.FromResult(Pass("Microsoft.NET.Test.Sdk is referenced."));
		}

		var testProjects = context.FindTestProjectFiles();

		foreach (var tp in testProjects)
		{
			var content = context.GetFileContent(tp);
			if (ReferencesPackageDirectly(content, "Microsoft.NET.Test.Sdk", includeVariants: true))
			{
				return Task.FromResult(Pass("Microsoft.NET.Test.Sdk is referenced."));
			}
		}

		return Task.FromResult(Fail(
			"Microsoft.NET.Test.Sdk is not referenced.",
			new RuleAdvisory
			{
				Summary = "Add Microsoft.NET.Test.Sdk to the test project.",
				Detail = $"""
					Microsoft.NET.Test.Sdk is not referenced in `Directory.Packages.props` or any test project.
					Add a reference at `{Standards.MicrosoftNetTestSdkVersion}`, which shares a version line with
					`{Standards.CodeCoveragePackage}` (see TST-04).

					Under Microsoft.Testing.Platform xunit.v3 provides its own entry point, so this package is no
					longer the thing that makes tests discoverable. It still sets `IsTestProject` and does no harm,
					which is why it remains recommended — but as a warning rather than a hard failure.
					""",
				Data = new()
				{
					["remediation_type"] = "add_package_version",
					["package_name"] = "Microsoft.NET.Test.Sdk",
					["package_version"] = Standards.MicrosoftNetTestSdkVersion,
					["file"] = "Directory.Packages.props"
				}
			}));
	}
}
