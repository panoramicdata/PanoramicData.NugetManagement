using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that README.md exists and is non-trivial.
/// </summary>
public class ReadmeExistsRule : RuleBase
{
	private const int _minReadmeLength = 200;

	/// <inheritdoc />
	public override string RuleId => "README-01";

	/// <inheritdoc />
	public override string RuleName => "README.md exists and is comprehensive";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.ReadmeBadges;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent("README.md");
		if (content is null)
		{
			return Task.FromResult(Fail(
				"README.md not found at repository root.",
				new RuleAdvisory
				{
					Summary = "Create a comprehensive README.md with badges, introduction, installation, and usage sections.",
					Detail = "No `README.md` file was found at the repository root. Create one with badges, introduction, installation, and usage sections.",
					Data = new() { ["expected_path"] = "README.md" }
				}));
		}

		return Task.FromResult(content.Length >= _minReadmeLength
			? Pass($"README.md found ({content.Length} characters).")
			: Fail(
				$"README.md is too short ({content.Length} characters, minimum {_minReadmeLength}).",
				new RuleAdvisory
				{
					Summary = "Expand README.md with introduction, installation, usage, and examples sections.",
					Detail = $"The `README.md` is only {content.Length} characters (minimum required: {_minReadmeLength}). Expand it with introduction, installation, usage, and examples sections.",
					Data = new() { ["file"] = "README.md", ["actual_length"] = content.Length, ["min_length"] = _minReadmeLength }
				}));
	}
}
