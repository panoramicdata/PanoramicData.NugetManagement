using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that CONTRIBUTING.md exists.
/// </summary>
public class ContributingMdExistsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "COM-02";

	/// <inheritdoc />
	public override string RuleName => "CONTRIBUTING.md exists";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CommunityHealth;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
		=> Task.FromResult(context.FileExists("CONTRIBUTING.md")
			? Pass("CONTRIBUTING.md found.")
			: Fail(
				"CONTRIBUTING.md not found at repository root.",
				new RuleAdvisory
				{
					Summary = "Create CONTRIBUTING.md with the standard contributing guide",
					Detail = "Create a `CONTRIBUTING.md` file at the repository root with the standard contributing guide.",
					Data = new()
					{
						["expected_path"] = "CONTRIBUTING.md",
						["template_content"] = Standards.ContributingMdContent
					}
				}));
}
