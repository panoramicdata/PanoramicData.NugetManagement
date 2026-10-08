using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that SECURITY.md exists and is the standard policy.
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
	{
		if (!context.FileExists("SECURITY.md"))
		{
			return Task.FromResult(Fail(
				"SECURITY.md not found at repository root.",
				new RuleAdvisory
				{
					Summary = "Create SECURITY.md with the standard security policy content",
					Detail = "Create a `SECURITY.md` file at the repository root with the standard security policy.",
					Data = new()
					{
						["expected_path"] = "SECURITY.md",
						["template_content"] = Standards.GetSecurityMdContent(context.FullName)
					}
				}));
		}

		// Null means the file was listed but never fetched, which says nothing about its contents.
		var content = context.GetFileContent("SECURITY.md");
		var standard = Standards.GetSecurityMdContent(context.FullName);
		if (content is not null && !Standards.IsGeneratedContent(content, standard))
		{
			return Task.FromResult(Fail(
				"SECURITY.md is not the standard security policy.",
				new RuleAdvisory
				{
					Summary = "Replace SECURITY.md with the standard private vulnerability reporting policy",
					Detail = "Every repository carries the same policy, which points reporters at GitHub private vulnerability "
						+ "reporting and publishes no mailbox address or bare URL for Codacy to flag. Hand-written variants "
						+ "grade poorly and drift apart, so the file is replaced. A repository that genuinely needs a "
						+ "different policy should waive this rule.",
					Data = new()
					{
						["remediation_type"] = "replace_file_content",
						["file"] = "SECURITY.md",
						["new_content"] = standard
					}
				}));
		}

		return Task.FromResult(Pass("SECURITY.md is the standard policy."));
	}
}
