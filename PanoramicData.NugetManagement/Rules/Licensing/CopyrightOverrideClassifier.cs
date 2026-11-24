using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Decides whether a project's Copyright override is purely our own notice. Anything that mentions
/// another holder, or contains text this class does not recognise, is treated as third-party
/// attribution and must never be flagged or edited.
/// </summary>
public static class CopyrightOverrideClassifier
{
	private static readonly Regex _year = new(
		@"(?<!\d)(?:19|20)\d{2}(?!\d)",
		RegexOptions.CultureInvariant,
		TimeSpan.FromSeconds(1));

	private static readonly Regex _yearExpression = new(
		@"\$\(\s*\[System\.DateTime\]::(?:Utc)?Now\.Year\s*\)",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
		TimeSpan.FromSeconds(1));

	private static readonly Regex _noticeWords = new(
		@"copyright|\(c\)|©|present",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
		TimeSpan.FromSeconds(1));

	/// <summary>
	/// True when the value names the holder and nothing else once years, the word Copyright, the
	/// (c) and copyright-sign marks, and range punctuation are ignored.
	/// </summary>
	/// <param name="value">The Copyright property text.</param>
	/// <param name="holder">Our copyright holder.</param>
	public static bool IsOwnNoticeOnly(string? value, string holder)
	{
		if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(holder))
		{
			return false;
		}

		var remainder = value.Trim();
		if (!remainder.Contains(holder, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		remainder = remainder.Replace(holder, " ", StringComparison.OrdinalIgnoreCase);
		remainder = _yearExpression.Replace(remainder, " ");
		remainder = _year.Replace(remainder, " ");
		remainder = _noticeWords.Replace(remainder, " ");
		remainder = remainder.Replace('-', ' ').Replace('–', ' ').Replace('—', ' ');

		return remainder.All(c => char.IsWhiteSpace(c) || c == '.' || c == ',');
	}

	/// <summary>
	/// True when the project file declares Copyright at least once and every declaration is
	/// <see cref="IsOwnNoticeOnly"/>. Content that cannot be parsed is never ours.
	/// </summary>
	/// <param name="projectXml">The project file content.</param>
	/// <param name="holder">Our copyright holder.</param>
	public static bool IsOwnOverride(string? projectXml, string holder)
	{
		var values = MsBuildProperties.TryGetValues(projectXml, "Copyright");
		return values is { Count: > 0 } && values.All(value => IsOwnNoticeOnly(value, holder));
	}
}
