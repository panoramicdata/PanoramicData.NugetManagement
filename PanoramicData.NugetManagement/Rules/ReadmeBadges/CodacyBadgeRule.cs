using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that README.md contains a Codacy badge.
/// </summary>
public class CodacyBadgeRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "README-02";

	/// <inheritdoc />
	public override string RuleName => "Codacy badge in README";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.ReadmeBadges;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent("README.md");
		return Task.FromResult(Contains(content, "codacy")
			? Pass("Codacy badge found in README.md.")
			: Fail(
				"README.md does not contain a Codacy badge.",
				new RuleAdvisory
				{
					Summary = "Add a Codacy badge link (from app.codacy.com) at the top of README.md.",
					Detail = "The `README.md` does not contain a Codacy badge. Add a Codacy badge link from `app.codacy.com` at the top of the file.",
					Data = new() { ["file"] = "README.md" }
				}));
	}
}
