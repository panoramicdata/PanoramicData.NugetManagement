using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Finds and corrects the year in copyright notices that name a given holder, so LIC-04 and its fix
/// agree on what a stale notice is.
/// </summary>
public static partial class CopyrightNotices
{
	/// <summary>One notice naming the holder, with the text it should have.</summary>
	/// <param name="Line">1-based line number.</param>
	/// <param name="Text">The line as found, without its line ending.</param>
	/// <param name="Corrected">The line with the current year.</param>
	public sealed record Notice(int Line, string Text, string Corrected)
	{
		/// <summary>Whether the year differs from the current one.</summary>
		public bool IsStale => !string.Equals(Text, Corrected, StringComparison.Ordinal);
	}

	/// <summary>
	/// Every dated notice naming <paramref name="holder"/>, outside fenced code. A year written as an
	/// MSBuild expression is not a literal year and is not found.
	/// </summary>
	/// <param name="content">The file content.</param>
	/// <param name="holder">The copyright holder.</param>
	/// <param name="year">The current year.</param>
	public static IReadOnlyList<Notice> Find(string content, string holder, int year)
	{
		var notices = new List<Notice>();
		var inFence = false;
		var lines = content.Split('\n');
		for (var i = 0; i < lines.Length; i++)
		{
			var line = lines[i].TrimEnd('\r');
			if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
			{
				inFence = !inFence;
			}
			else if (!inFence && HolderFollowsYear(line, holder))
			{
				notices.Add(new Notice(i + 1, line, Correct(line, holder, year)));
			}
		}

		return notices;
	}

	/// <summary>
	/// The content with the year of every notice naming <paramref name="holder"/> set to
	/// <paramref name="year"/>: a single year becomes it, a range keeps its first year. Line endings
	/// and everything else are untouched.
	/// </summary>
	/// <param name="content">The file content.</param>
	/// <param name="holder">The copyright holder.</param>
	/// <param name="year">The current year.</param>
	public static string Correct(string content, string holder, int year)
	{
		var inFence = false;
		var lines = content.Split('\n');
		for (var i = 0; i < lines.Length; i++)
		{
			if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
			{
				inFence = !inFence;
			}
			else if (!inFence && HolderFollowsYear(lines[i], holder))
			{
				lines[i] = NoticeRegex().Replace(lines[i], match => Rewrite(match, year), count: 1);
			}
		}

		return string.Join('\n', lines);
	}

	/// <summary>Whether the first dated notice on the line names the holder directly after the year.</summary>
	private static bool HolderFollowsYear(string line, string holder)
		=> NoticeRegex().Match(line) is { Success: true } match
			&& line[(match.Index + match.Length)..].TrimStart(' ', '\t', ',', '.', ':') is var rest
			&& (rest.StartsWith(holder, StringComparison.OrdinalIgnoreCase)
				|| rest.StartsWith("by " + holder, StringComparison.OrdinalIgnoreCase));

	private static string Rewrite(Match match, int year)
	{
		var first = int.Parse(match.Groups["first"].Value, System.Globalization.CultureInfo.InvariantCulture);
		var last = match.Groups["last"].Success
			? int.Parse(match.Groups["last"].Value, System.Globalization.CultureInfo.InvariantCulture)
			: first;

		if (last == year && first <= year)
		{
			return match.Value;
		}

		var lead = match.Groups["lead"].Value;
		return first < year && match.Groups["last"].Success
			? $"{lead}{match.Groups["first"].Value}{match.Groups["dash"].Value}{year}"
			: $"{lead}{year}";
	}

	[GeneratedRegex(@"(?<![\w])(?<lead>(?:copyright|\(c\)|©)(?:\s*(?:\(c\)|©))?\s*)(?<first>(?:19|20)\d{2})(?:(?<dash>\s*[-–]\s*)(?<last>(?:19|20)\d{2}))?(?!\d)", RegexOptions.IgnoreCase)]
	private static partial Regex NoticeRegex();
}
