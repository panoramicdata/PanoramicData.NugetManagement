using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that SECURITY.md exists.
/// </summary>
public class SecurityMdExistsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "COM-01";

	/// <inheritdoc />
	public override string RuleName => "SECURITY.md exists";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CommunityHealth;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
		=> Task.FromResult(context.FileExists("SECURITY.md")
			? Pass("SECURITY.md found.")
			: Fail(
				"SECURITY.md not found at repository root.",
				new RuleAdvisory
				{
					Summary = "Create SECURITY.md with the standard security policy content",
					Detail = "Create a `SECURITY.md` file at the repository root with the standard security policy.",
					Data = new()
					{
						["expected_path"] = "SECURITY.md",
						["template_content"] = Standards.SecurityMdContent
					}
				}));
}
