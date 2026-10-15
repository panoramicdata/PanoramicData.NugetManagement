using System.Globalization;
using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// Reads <c>git diff -U0</c> output into the new-side lines each file gained.
/// </summary>
public static partial class ChangedLineParser
{
	[GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(?<start>\d+)(?:,(?<count>\d+))? @@")]
	private static partial Regex HunkHeader();

	/// <summary>
	/// Parses a zero-context unified diff.
	/// </summary>
	/// <param name="unifiedZeroDiff">The output of <c>git diff -U0</c>.</param>
	/// <returns>One entry per file that still exists, with the lines it gained or changed.</returns>
	public static IReadOnlyList<ChangedFile> Parse(string unifiedZeroDiff)
	{
		var result = new List<ChangedFile>();
		HashSet<int>? lines = null;

		foreach (var line in unifiedZeroDiff.Split('\n').Select(l => l.TrimEnd('\r')))
		{
			if (line.StartsWith("+++ ", StringComparison.Ordinal))
			{
				var target = line[4..];
				var path = target == "/dev/null"
					? null
					: target.StartsWith("b/", StringComparison.Ordinal) ? target[2..] : target;

				lines = path is null ? null : [];

				if (path is not null)
				{
					result.Add(new ChangedFile(path, lines!));
				}

				continue;
			}

			var match = HunkHeader().Match(line);

			if (!match.Success || lines is null)
			{
				continue;
			}

			var start = int.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture);
			var count = match.Groups["count"].Success
				? int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture)
				: 1;

			for (var offset = 0; offset < count; offset++)
			{
				lines.Add(start + offset);
			}
		}

		return result;
	}
}
