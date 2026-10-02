using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that SECURITY.md exists, and is not the earlier generated version that published a
/// mailbox address.
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

		var content = context.GetFileContent("SECURITY.md");
		if (content is not null && Standards.IsGeneratedContent(content, Standards.LegacySecurityMdContent))
		{
			return Task.FromResult(Fail(
				"SECURITY.md is the earlier generated policy, which publishes a mailbox address that Codacy flags as PII.",
				new RuleAdvisory
				{
					Summary = "Replace the generated SECURITY.md with the private vulnerability reporting policy",
					Detail = "The file is exactly what this tool previously generated, so it is replaced; a hand-written policy is never touched.",
					Data = new()
					{
						["remediation_type"] = "replace_file_content",
						["file"] = "SECURITY.md",
						["new_content"] = Standards.GetSecurityMdContent(context.FullName)
					}
				}));
		}

		return Task.FromResult(Pass("SECURITY.md found."));
	}
}
