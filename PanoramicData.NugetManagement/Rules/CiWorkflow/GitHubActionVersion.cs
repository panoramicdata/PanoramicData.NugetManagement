using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Helpers for reading and comparing GitHub Action major versions used in a workflow file.
/// Comparisons are "at least" the expected floor, so a repository that is *ahead* of the standard
/// (e.g. on <c>@v6</c> when the floor is <c>@v5</c>) is treated as compliant, not flagged as wrong.
/// </summary>
public static partial class GitHubActionVersion
{
	/// <summary>
	/// Gets the highest major version at which an action is referenced in the workflow content,
	/// or null when the action is not referenced.
	/// </summary>
	/// <param name="content">The workflow file content.</param>
	/// <param name="actionName">The action, e.g. "actions/checkout".</param>
	public static int? GetHighestUsedMajor(string content, string actionName)
	{
		if (string.IsNullOrEmpty(content))
		{
			return null;
		}

		var regex = new Regex(Regex.Escape(actionName) + @"@v(\d+)", RegexOptions.IgnoreCase);
		var majors = regex.Matches(content)
			.Select(m => int.TryParse(m.Groups[1].Value, out var v) ? v : -1)
			.Where(v => v >= 0)
			.ToList();

		return majors.Count == 0 ? null : majors.Max();
	}

	/// <summary>
	/// Parses the major version number from a spec like "v6" (returns 6). Returns 0 when unparseable.
	/// </summary>
	public static int ParseMajor(string versionSpec)
	{
		if (string.IsNullOrWhiteSpace(versionSpec))
		{
			return 0;
		}

		var digits = versionSpec.AsSpan().TrimStart('v').TrimStart('V');
		return int.TryParse(digits, out var v) ? v : 0;
	}

	/// <summary>
	/// Determines whether the workflow uses an action at or above the expected floor version.
	/// </summary>
	/// <param name="content">The workflow content.</param>
	/// <param name="actionName">The action, e.g. "actions/checkout".</param>
	/// <param name="floorVersionSpec">The minimum acceptable version spec, e.g. "v6".</param>
	/// <param name="usedMajor">The highest major version found, or null when the action is absent.</param>
	public static bool UsesAtLeast(string content, string actionName, string floorVersionSpec, out int? usedMajor)
	{
		usedMajor = GetHighestUsedMajor(content, actionName);
		return usedMajor is not null && usedMajor >= ParseMajor(floorVersionSpec);
	}
}
