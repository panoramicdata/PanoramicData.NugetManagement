using System.Globalization;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Web.Remediations.Licensing;

/// <summary>Sets the year in copyright notices to the current year.</summary>
public sealed class CopyrightYearRemediation : DataDrivenRemediation
{
	private const string _type = "update_copyright_year";

	/// <inheritdoc />
	public override string RuleId => "LIC-04";

	/// <inheritdoc />
	public override bool CanRemediate(RuleResult result)
		=> !result.Passed
			&& result.Advisory is { } advisory
			&& advisory.Data.TryGetValue("remediation_type", out var type) && type as string == _type
			&& ReadStrings(advisory.Data, "files").Length > 0
			&& ReadStrings(advisory.Data, "holder").Length > 0
			&& ReadYear(advisory.Data) is not null;

	/// <inheritdoc />
	protected override void ApplyCore(
		string localPath,
		RuleResult result,
		Dictionary<string, object> data,
		string remediationType,
		List<string> applied,
		Action<string>? onOutput)
	{
		if (remediationType != _type || ReadYear(data) is not { } year || ReadStrings(data, "holder") is not [var holder, ..])
		{
			return;
		}

		foreach (var file in ReadStrings(data, "files"))
		{
			if (RepositoryFileText.Read(localPath, file) is not var (text, encoding))
			{
				onOutput?.Invoke($"⏭️ [{result.RuleId}] {file} is missing or outside the repository — skipping.");
				continue;
			}

			var updated = CopyrightNotices.Correct(text, holder, year);
			if (updated == text)
			{
				onOutput?.Invoke($"⏭️ [{result.RuleId}] {file} already carries {year} — skipping.");
				continue;
			}

			RepositoryFileText.Write(localPath, file, updated, encoding);
			applied.Add(file);
			onOutput?.Invoke($"✅ [{result.RuleId}] Set the copyright year in {file} to {year}");
		}
	}

	private static int? ReadYear(Dictionary<string, object> data)
		=> ReadStrings(data, "year") is [var text, ..]
			&& int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
			&& year is >= 1900 and <= 9999
				? year
				: null;
}
