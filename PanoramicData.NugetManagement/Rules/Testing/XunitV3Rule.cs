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
		if (Contains(dirPackages, "xunit.v3"))
		{
			return Task.FromResult(Pass("xUnit v3 is referenced."));
		}

		// Check individual test .csproj files
		var testProjects = context.FindFiles(".csproj")
			.Where(f => f.Contains(".Test", StringComparison.OrdinalIgnoreCase));

		foreach (var tp in testProjects)
		{
			var content = context.GetFileContent(tp);
			if (Contains(content, "xunit.v3"))
			{
				return Task.FromResult(Pass("xUnit v3 is referenced."));
			}
		}

		return Task.FromResult(Fail(
			"xUnit v3 is not referenced. Legacy xUnit v2 may be in use.",
			new RuleAdvisory
			{
				Summary = "Replace xunit/xunit.core/xunit.runner.visualstudio v2 references with xunit.v3 and xunit.runner.visualstudio v3.",
				Detail = "xUnit v3 is not referenced in `Directory.Packages.props` or any test project. Replace legacy `xunit`/`xunit.core`/`xunit.runner.visualstudio` v2 references with `xunit.v3` and `xunit.runner.visualstudio` v3.",
				Data = new() { ["expected_package"] = "xunit.v3" }
			}));
	}
}
