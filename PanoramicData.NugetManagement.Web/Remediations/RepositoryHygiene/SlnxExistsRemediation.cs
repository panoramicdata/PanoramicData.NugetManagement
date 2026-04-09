using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations.RepositoryHygiene;

/// <summary>
/// Ensures a root .slnx exists by migrating a legacy root .sln when available.
/// </summary>
public sealed class SlnxExistsRemediation : IRemediation
{
	/// <inheritdoc />
	public string RuleId => "REPO-04";

	/// <inheritdoc />
	public bool CanRemediate(RuleResult result)
		=> !result.Passed && result.Advisory is not null;

	/// <inheritdoc />
	public void Apply(string localPath, RuleResult result, List<string> applied, Action<string>? onOutput)
	{
		_ = RemediationHelpers.EnsureSlnxFromLegacySolution(localPath, result, applied, onOutput);
	}
}
