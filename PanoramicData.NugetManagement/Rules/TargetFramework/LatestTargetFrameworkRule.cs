using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that projects target the latest .NET version.
/// </summary>
public class LatestTargetFrameworkRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "TFM-01";

	/// <inheritdoc />
	public override string RuleName => "Latest .NET target framework";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.TargetFramework;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var csprojFiles = context.FindFiles(".csproj").ToList();
		var outdated = new List<string>();

		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (content is null)
			{
				continue;
			}

			if (!Contains(content, $"<TargetFramework>{Standards.LatestTargetFramework}</TargetFramework>"))
			{
				outdated.Add(csproj);
			}
		}

		return Task.FromResult(outdated.Count == 0
			? Pass($"All projects target {Standards.LatestTargetFramework}.")
			: Fail(
				$"The following projects do not target {Standards.LatestTargetFramework}: {string.Join(", ", outdated)}",
				new RuleAdvisory
				{
					Summary = $"Update <TargetFramework> to {Standards.LatestTargetFramework} in all .csproj files.",
					Detail = $"The following projects do not target `{Standards.LatestTargetFramework}`: {string.Join(", ", outdated)}. Update the `<TargetFramework>` element in each `.csproj` file.",
					Data = new()
					{
						["projects"] = outdated.ToArray(),
						["latest_tfm"] = Standards.LatestTargetFramework,
						["remediation_type"] = "ensure_csproj_property",
						["property_name"] = "TargetFramework",
						["property_value"] = Standards.LatestTargetFramework
					}
				}));
	}
}
