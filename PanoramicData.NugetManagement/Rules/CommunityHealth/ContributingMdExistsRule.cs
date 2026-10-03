using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that CONTRIBUTING.md exists and is the standard one.
/// </summary>
/// <remarks>
/// Held to the standard text, not just to existing, so the whole estate tells contributors the same
/// thing and CLAUDE.md can refer to the file knowing it is there. A repository with a genuine reason
/// to differ waives the rule rather than drifting.
/// </remarks>
public class ContributingMdExistsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "COM-02";

	/// <inheritdoc />
	public override string RuleName => "CONTRIBUTING.md matches the standard";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CommunityHealth;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (!context.FileExists("CONTRIBUTING.md"))
		{
			return Task.FromResult(Fail(
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

		var content = context.GetFileContent("CONTRIBUTING.md");
		if (content is null || Standards.IsGeneratedContent(content, Standards.ContributingMdContent))
		{
			return Task.FromResult(Pass("CONTRIBUTING.md matches the standard."));
		}

		return Task.FromResult(Fail(
			"CONTRIBUTING.md differs from the standard contributing guide.",
			new RuleAdvisory
			{
				Summary = "Replace CONTRIBUTING.md with the standard contributing guide",
				Detail = "Every repository carries the same CONTRIBUTING.md. Replace this one with the standard text, or waive COM-02 for this repository if it genuinely needs its own.",
				Data = new()
				{
					["remediation_type"] = "replace_file_content",
					["file"] = "CONTRIBUTING.md",
					["new_content"] = Standards.ContributingMdContent
				}
			}));
	}
}
