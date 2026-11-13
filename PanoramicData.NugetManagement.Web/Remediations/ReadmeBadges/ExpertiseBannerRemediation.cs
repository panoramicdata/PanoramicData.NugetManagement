using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Web.Remediations.ReadmeBadges;

/// <summary>Places the expertise statement under the title and badges in README.md.</summary>
public sealed class ExpertiseBannerRemediation : DataDrivenRemediation
{
	private const string _type = "insert_readme_banner";

	/// <inheritdoc />
	public override string RuleId => "README-05";

	/// <inheritdoc />
	public override bool CanRemediate(RuleResult result)
		=> !result.Passed
			&& result.Advisory is { } advisory
			&& advisory.Data.TryGetValue("remediation_type", out var type) && type as string == _type
			&& ReadStrings(advisory.Data, "file").Length > 0
			&& ReadStrings(advisory.Data, "banner").Length > 0;

	/// <inheritdoc />
	protected override void ApplyCore(
		string localPath,
		RuleResult result,
		Dictionary<string, object> data,
		string remediationType,
		List<string> applied,
		Action<string>? onOutput)
	{
		if (remediationType != _type)
		{
			return;
		}

		var file = ReadStrings(data, "file")[0];
		var banner = ReadStrings(data, "banner")[0];
		var path = Path.Combine(localPath, file);
		if (!File.Exists(path))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {file} does not exist — cannot add the statement.");
			return;
		}

		var content = File.ReadAllText(path);
		if (ReadmeExpertiseBanner.IsAtTop(content, banner))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {file} already opens with the statement — skipping.");
			return;
		}

		File.WriteAllText(path, ReadmeExpertiseBanner.Insert(content, banner));
		applied.Add(file);
		onOutput?.Invoke($"✅ [{result.RuleId}] Added the expertise statement to {file}");
	}
}
