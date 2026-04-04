using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that .gitignore exists and covers essential entries.
/// </summary>
public class GitignoreExistsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "REPO-01";

	/// <inheritdoc />
	public override string RuleName => ".gitignore exists with essentials";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.RepositoryHygiene;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent(".gitignore");
		if (content is null)
		{
			return Task.FromResult(Fail(
				".gitignore not found.",
				new RuleAdvisory
				{
					Summary = "Create a comprehensive .gitignore for .NET projects.",
					Detail = "No `.gitignore` file was found at the repository root. Create one with standard .NET entries including `[Bb]in/`, `[Oo]bj/`, and `.vs/`.",
					Data = new() { ["expected_path"] = ".gitignore", ["template_content"] = Standards.GitignoreContent }
				}));
		}

		var hasBin = Contains(content, "[Bb]in") || Contains(content, "bin/");
		var hasObj = Contains(content, "[Oo]bj") || Contains(content, "obj/");
		var hasVs = Contains(content, ".vs/");

		var missingEntries = new List<string>();
		if (!hasBin)
		{
			missingEntries.Add("[Bb]in/");
		}

		if (!hasObj)
		{
			missingEntries.Add("[Oo]bj/");
		}

		if (!hasVs)
		{
			missingEntries.Add(".vs/");
		}

		return Task.FromResult(hasBin && hasObj && hasVs
			? Pass(".gitignore found with essential entries (bin, obj, .vs).")
			: Fail(
				".gitignore is missing essential entries (bin/, obj/, .vs/).",
				new RuleAdvisory
				{
					Summary = "Add [Bb]in/, [Oo]bj/, and .vs/ entries to .gitignore.",
					Detail = "The `.gitignore` file is missing one or more essential entries. Ensure `[Bb]in/`, `[Oo]bj/`, and `.vs/` are all present.",
					Data = new()
					{
						["file"] = ".gitignore",
						["remediation_type"] = "append_lines",
						["lines"] = missingEntries.ToArray()
					}
				}));
	}
}
