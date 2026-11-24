using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Web.Remediations.Licensing;

/// <summary>
/// Removes our own Copyright from project files so the Directory.Build.props value applies. Re-reads
/// each file and skips any whose value is third-party attribution, whatever the advisory lists.
/// </summary>
public sealed class CopyrightOverrideRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "LIC-05";

	/// <inheritdoc />
	protected override void ApplyCore(
		string localPath,
		RuleResult result,
		Dictionary<string, object> data,
		string remediationType,
		List<string> applied,
		Action<string>? onOutput)
	{
		var holder = data.TryGetValue("holder", out var h) && h is string name && !string.IsNullOrWhiteSpace(name)
			? name
			: Standards.CopyrightHolder;

		var ours = new List<string>();
		foreach (var project in ReadStrings(data, "projects"))
		{
			if (RepositoryFileText.Read(localPath, project) is var (text, _) && CopyrightOverrideClassifier.IsOwnOverride(text, holder))
			{
				ours.Add(project);
			}
			else
			{
				onOutput?.Invoke($"⏭️ [{result.RuleId}] {project} has a Copyright that is not only our own notice - leaving it alone.");
			}
		}

		var safe = new Dictionary<string, object>(data) { ["projects"] = ours.ToArray() };
		base.ApplyCore(localPath, result, safe, remediationType, applied, onOutput);
	}
}
