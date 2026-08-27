using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that xUnit v3 is referenced (not xUnit v2).
/// </summary>
public class XunitV3Rule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "TST-02";

	/// <inheritdoc />
	public override string RuleName => "xUnit v3 referenced";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Testing;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var dirPackages = context.GetFileContent("Directory.Packages.props");

		// Check for xunit.v3 in centralized packages
		if (ReferencesPackage(dirPackages, "xunit.v3", includeVariants: true))
		{
			return Task.FromResult(Pass("xUnit v3 is referenced."));
		}

		// Check individual test .csproj files
		var testProjects = context.FindTestProjectFiles();

		foreach (var tp in testProjects)
		{
			var content = context.GetFileContent(tp);
			if (ReferencesPackageDirectly(content, "xunit.v3", includeVariants: true))
			{
				return Task.FromResult(Pass("xUnit v3 is referenced."));
			}
		}

		return Task.FromResult(Fail(
			"xUnit v3 is not referenced. Legacy xUnit v2 may be in use.",
			new RuleAdvisory
			{
				Summary = $"Replace xunit/xunit.core/xunit.runner.visualstudio v2 references with xunit.v3 {Standards.XunitV3Version}.",
				Detail = $"""
					xUnit v3 is not referenced in `Directory.Packages.props` or any test project. Replace legacy
					`xunit`/`xunit.core` v2 references with `xunit.v3` at `{Standards.XunitV3Version}`.

					Drop `{Standards.VsTestAdapterPackage}` rather than upgrading it: it is the VSTest adapter, and
					xunit.v3 runs on Microsoft.Testing.Platform, which does not use it. Removing it entirely leaves
					tests discovered and run as before, in CI as well as locally.

					xunit.v3 4.x depends on Microsoft.Testing.Platform 2.3 or later, which has no VSTest bridge on
					the .NET 10 SDK, so `dotnet test` needs the `test.runner` opt-in in `global.json` — see TST-06.
					""",
				Data = new()
				{
					["remediation_type"] = "add_package_version",
					["package_name"] = "xunit.v3",
					["package_version"] = Standards.XunitV3Version,
					["file"] = "Directory.Packages.props"
				}
			}));
	}
}
