namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Renders, finds and places the expertise statement at the top of a package README.
/// </summary>
public static class ReadmeExpertiseBanner
{
	private const string _packageNamePlaceholder = "{PackageName}";

	/// <summary>
	/// The banner line for a repository, with the template's placeholders filled in.
	/// </summary>
	/// <param name="template">The configured template.</param>
	/// <param name="packageName">The repository name.</param>
	public static string Render(string template, string packageName)
		=> template.Trim().Replace(_packageNamePlaceholder, packageName, StringComparison.Ordinal);

	/// <summary>
	/// Whether the banner is present and nothing but the title and badges precedes it, so that it is
	/// seen before the first section.
	/// </summary>
	/// <param name="readme">The README content.</param>
	/// <param name="banner">The rendered banner.</param>
	public static bool IsAtTop(string readme, string banner)
	{
		var lines = SplitLines(readme);
		var index = lines.FindIndex(line => Normalise(line) == Normalise(banner));
		return index >= 0 && lines.Take(index).All(IsHeaderLine);
	}

	/// <summary>
	/// Returns the README with the banner placed directly after the title and badges, removing any
	/// copy already further down. Line endings are kept as the file has them.
	/// </summary>
	/// <param name="readme">The README content.</param>
	/// <param name="banner">The rendered banner.</param>
	public static string Insert(string readme, string banner)
	{
		var newLine = readme.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
		var lines = SplitLines(readme).Where(line => Normalise(line) != Normalise(banner)).ToList();

		var at = 0;
		while (at < lines.Count && IsHeaderLine(lines[at]))
		{
			at++;
		}

		// Drop the blank lines the header ended with: the block below supplies its own.
		var end = at;
		while (at > 0 && string.IsNullOrWhiteSpace(lines[at - 1]))
		{
			at--;
		}

		lines.RemoveRange(at, end - at);

		var block = new List<string>();
		if (at > 0)
		{
			block.Add(string.Empty);
		}

		block.Add(banner);
		block.Add(string.Empty);

		lines.InsertRange(at, block);

		var result = string.Join(newLine, lines);
		return result.EndsWith(newLine, StringComparison.Ordinal) ? result : result + newLine;
	}

	private static List<string> SplitLines(string text)
		=> [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];

	private static string Normalise(string text)
		=> string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

	/// <summary>
	/// A line that belongs to the opening of a README: blank, the top-level title, a badge, or an
	/// HTML logo line.
	/// </summary>
	private static bool IsHeaderLine(string line)
	{
		var trimmed = line.TrimStart();
		return trimmed.Length == 0
			|| trimmed.StartsWith("# ", StringComparison.Ordinal)
			|| trimmed.StartsWith("[![", StringComparison.Ordinal)
			|| trimmed.StartsWith("![", StringComparison.Ordinal)
			|| trimmed.StartsWith("<img", StringComparison.OrdinalIgnoreCase)
			|| trimmed.StartsWith("<p", StringComparison.OrdinalIgnoreCase)
			|| trimmed.StartsWith("<a ", StringComparison.OrdinalIgnoreCase);
	}
}
