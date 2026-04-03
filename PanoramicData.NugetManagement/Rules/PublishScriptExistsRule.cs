using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that Publish.ps1 exists at the repository root.
/// </summary>
public class PublishScriptExistsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CI-07";

	/// <inheritdoc />
	public override string RuleName => "Publish.ps1 exists";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CiCd;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (!context.Options.IsPackable)
		{
			return Task.FromResult(Pass("Repository is not packable — Publish.ps1 not required."));
		}

		return Task.FromResult(context.FileExists("Publish.ps1")
			? Pass("Publish.ps1 found.")
			: Fail(
				"Publish.ps1 not found at repository root.",
				new RuleAdvisory
				{
					Summary = "Add standard Publish.ps1 script for tag-based publishing",
					Detail = "Add a `Publish.ps1` script that checks for a clean working tree, uses `nbgv get-version` to determine the version, creates a git tag, and pushes the tag to trigger trusted publishing in CI.",
					Data = new() { ["expected_path"] = "Publish.ps1" }
				}));
	}
}
