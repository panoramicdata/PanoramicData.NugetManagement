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
					Data = new() { ["expected_path"] = ".gitignore" }
				}));
		}

		var hasBin = Contains(content, "[Bb]in") || Contains(content, "bin/");
		var hasObj = Contains(content, "[Oo]bj") || Contains(content, "obj/");
		var hasVs = Contains(content, ".vs/");

		return Task.FromResult(hasBin && hasObj && hasVs
			? Pass(".gitignore found with essential entries (bin, obj, .vs).")
			: Fail(
				".gitignore is missing essential entries (bin/, obj/, .vs/).",
				new RuleAdvisory
				{
					Summary = "Add [Bb]in/, [Oo]bj/, and .vs/ entries to .gitignore.",
					Detail = "The `.gitignore` file is missing one or more essential entries. Ensure `[Bb]in/`, `[Oo]bj/`, and `.vs/` are all present.",
					Data = new() { ["file"] = ".gitignore" }
				}));
	}
}

/// <summary>
/// Checks that nuget-key.txt is listed in .gitignore.
/// </summary>
public class NugetKeyGitignoredRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "REPO-02";

	/// <inheritdoc />
	public override string RuleName => "nuget-key.txt is gitignored";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.RepositoryHygiene;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent(".gitignore");
		return Task.FromResult(Contains(content, "nuget-key.txt")
			? Pass("nuget-key.txt is in .gitignore.")
			: Fail(
				"nuget-key.txt is not in .gitignore — risk of leaking NuGet API key.",
				new RuleAdvisory
				{
					Summary = "Add 'nuget-key.txt' to .gitignore.",
					Detail = "The `.gitignore` file does not include `nuget-key.txt`. This risks leaking a NuGet API key. Add `nuget-key.txt` to `.gitignore`.",
					Data = new()
					{
						["file"] = ".gitignore",
						["remediation_type"] = "append_line",
						["line_content"] = "nuget-key.txt"
					}
				}));
	}
}

/// <summary>
/// Checks that NeutralResourcesLanguage is set.
/// </summary>
public class NeutralResourcesLanguageRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "REPO-03";

	/// <inheritdoc />
	public override string RuleName => "NeutralResourcesLanguage set";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.RepositoryHygiene;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var csprojFiles = context.FindFiles(".csproj")
			.Where(f => !f.Contains(".Test", StringComparison.OrdinalIgnoreCase))
			.ToList();

		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (content is not null && !Contains(content, "<NeutralResourcesLanguage>"))
			{
				return Task.FromResult(Fail(
					$"{csproj} does not set NeutralResourcesLanguage.",
					new RuleAdvisory
					{
						Summary = "Add <NeutralResourcesLanguage>en</NeutralResourcesLanguage> to the .csproj.",
						Detail = $"The project `{csproj}` does not set `NeutralResourcesLanguage`. Add `<NeutralResourcesLanguage>en</NeutralResourcesLanguage>` to a `<PropertyGroup>`.",
						Data = new()
						{
							["file"] = csproj,
							["remediation_type"] = "ensure_csproj_property",
							["property_name"] = "NeutralResourcesLanguage",
							["property_value"] = "en"
						}
					}));
			}
		}

		return Task.FromResult(Pass("All non-test projects have NeutralResourcesLanguage set."));
	}
}
