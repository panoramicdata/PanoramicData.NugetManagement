using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that README.md contains a license badge.
/// </summary>
public class LicenseBadgeRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "README-04";

	/// <inheritdoc />
	public override string RuleName => "License badge in README";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.ReadmeBadges;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var license = context.Options.ExpectedLicense;
		var content = context.GetFileContent("README.md");
		var badgeLine = LicenseExpressions.BadgeLine(license);
		return Task.FromResult(
			LicenseExpressions.BadgeMarkers(license).Any(marker => HasMarker(content, marker))
			? Pass($"License badge for \"{license}\" found in README.md.")
			: Fail(
				$"README.md does not contain a license badge for \"{license}\".",
				new RuleAdvisory
				{
					Summary = $"Add a license badge (e.g. shields.io {license} badge) at the top of README.md.",
					Detail = $"The `README.md` does not contain a license badge for `{license}`. Add a shields.io badge such as `{badgeLine}`.",
					Data = new()
					{
						["file"] = "README.md",
						["expected_license"] = license,
						["remediation_type"] = "prepend_line",
						["line_content"] = badgeLine
					}
				}));
	}

	// The marker must end where the label does, or "License: MIT" would pass a "License: MIT AND BSD-2-Clause" badge.
	private static bool HasMarker(string? content, string marker)
	{
		if (content is null)
		{
			return false;
		}

		var index = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
		while (index >= 0)
		{
			var end = index + marker.Length;
			var isSingleDash = end < content.Length && content[end] == '-'
				&& (end + 1 >= content.Length || content[end + 1] != '-');
			if (end >= content.Length || isSingleDash || content[end] is ']' or ')' or '"' or '\'' or '.' or '?' or '&' or '/' or '\r' or '\n')
			{
				return true;
			}

			index = content.IndexOf(marker, index + 1, StringComparison.OrdinalIgnoreCase);
		}

		return false;
	}
}
