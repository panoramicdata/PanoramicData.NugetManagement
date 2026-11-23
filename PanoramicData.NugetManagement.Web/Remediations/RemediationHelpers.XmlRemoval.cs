using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations;

internal static partial class RemediationHelpers
{
	/// <summary>
	/// Deletes every line-leading element of the named property from a project or props file, keeping
	/// the file's encoding and line endings. An element sharing a line with other markup is left alone.
	/// </summary>
	public static void RemoveXmlProperty(
		string localPath,
		string relativePath,
		string propertyName,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		if (RepositoryFileText.Read(localPath, relativePath) is not var (text, encoding))
		{
			Say(onOutput, $"⏭️ [{result.RuleId}] {relativePath} is missing or outside the repository — skipping.");
			return;
		}

		var element = new Regex(
			$@"^[ \t]*<{Regex.Escape(propertyName)}(?:\s[^>]*)?>[^<]*</{Regex.Escape(propertyName)}>[ \t]*(?:\r\n|\n)?",
			RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
			TimeSpan.FromSeconds(1));

		var updated = element.Replace(text, string.Empty);
		if (updated == text)
		{
			Say(onOutput, $"⏭️ [{result.RuleId}] {relativePath} has no <{propertyName}> on a line of its own — skipping.");
			return;
		}

		RepositoryFileText.Write(localPath, relativePath, updated, encoding);
		applied.Add(relativePath);
		Say(onOutput, $"✅ [{result.RuleId}] Removed <{propertyName}> from {relativePath}");
	}
}
