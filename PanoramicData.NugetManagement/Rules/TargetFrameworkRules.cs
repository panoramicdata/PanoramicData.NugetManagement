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
				$"Update <TargetFramework> to {Standards.LatestTargetFramework} in all .csproj files."));
	}
}
