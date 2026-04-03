using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that a .slnx solution file exists in the repository root.
/// </summary>
public class SlnxExistsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "REPO-04";

	/// <inheritdoc />
	public override string RuleName => ".slnx solution file exists";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.RepositoryHygiene;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var slnxFiles = context.FindFiles(".slnx")
			.Where(f => !f.Contains('/'))
			.ToList();

		return Task.FromResult(slnxFiles.Count > 0
			? Pass($"Solution file found: {slnxFiles[0]}.")
			: Fail(
				"No .slnx solution file found at repository root.",
				new RuleAdvisory
				{
					Summary = "Create an SDK-style .slnx solution file at the repository root.",
					Detail = "No `.slnx` solution file was found at the repository root. Create an SDK-style `.slnx` solution file.",
					Data = new() { ["expected_extension"] = ".slnx" }
				}));
	}
}
