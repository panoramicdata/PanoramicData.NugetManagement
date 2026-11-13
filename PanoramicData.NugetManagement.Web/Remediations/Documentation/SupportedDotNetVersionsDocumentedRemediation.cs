using System.Text;
using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations.Documentation;

/// <summary>
/// Writes the supported-versions sentence into README.md and brings stale .NET version mentions up to
/// the one version the repository supports.
/// </summary>
/// <remarks>
/// Works from the data the rule emits and nothing else. The sentence is always safe to add. A mention is
/// rewritten only when exactly one modern .NET version is supported and the mention is a plain modern
/// version ("<c>.NET 10</c>", "<c>net10.0</c>"): with several supported versions there is no single
/// right replacement, and .NET Framework, Standard and Core mentions, target-specific monikers such as
/// <c>net10.0-windows</c> and historical statements need a person or the AI, so those are left and
/// the rule keeps failing for them.
/// </remarks>
public sealed partial class SupportedDotNetVersionsDocumentedRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "DOC-02";

	/// <inheritdoc />
	/// <remarks>The rule emits its findings, not a <c>remediation_type</c>, so the data-driven check does not apply.</remarks>
	public override bool CanRemediate(RuleResult result)
		=> !result.Passed && Phrase(result) is not null;

	/// <inheritdoc />
	public override void Apply(string localPath, RuleResult result, List<string> applied, Action<string>? onOutput)
	{
		var phrase = Phrase(result);
		if (phrase is null)
		{
			return;
		}

		if (InsertPhrase(Path.Combine(localPath, "README.md"), phrase))
		{
			applied.Add("README.md");
			onOutput?.Invoke($"✅ README.md: added \"{phrase}.\"");
		}

		var current = SingleCurrentVersion(Strings(result, "supported_versions"));
		if (current is null)
		{
			onOutput?.Invoke("ℹ️ Several versions (or a non-.NET family) are supported, so stale mentions are left for a person or the AI.");
			return;
		}

		foreach (var file in Strings(result, "unsupported_mentions").Select(SourceFile).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (RewriteMentions(Path.Combine(localPath, file), current))
			{
				if (!applied.Contains(file, StringComparer.OrdinalIgnoreCase))
				{
					applied.Add(file);
				}

				onOutput?.Invoke($"✅ {file}: updated stale .NET versions to {current}");
			}
		}
	}

	private static string? Phrase(RuleResult result)
		=> Strings(result, "required_phrase") is [{ Length: > 0 } phrase, ..] ? phrase : null;

	private static string[] Strings(RuleResult result, string key)
		=> result.Advisory is { } advisory ? ReadStrings(advisory.Data, key) : [];

	/// <summary>
	/// The version number to write ("11.0") when the repository supports exactly one plain .NET version.
	/// </summary>
	private static string? SingleCurrentVersion(string[] supported)
		=> supported is [var only] && CurrentVersionRegex().Match(only) is { Success: true } match
			? match.Groups["version"].Value
			: null;

	/// <summary>
	/// The file a mention was found in: the text before the first ": ", without a trailing "&lt;Property&gt;".
	/// </summary>
	private static string SourceFile(string mention)
	{
		var separator = mention.IndexOf(": ", StringComparison.Ordinal);
		var source = separator < 0 ? mention : mention[..separator];
		return PropertySuffixRegex().Replace(source, string.Empty).Replace('\\', '/');
	}

	/// <summary>
	/// Adds the sentence after the title and any badge lines that follow it. False when it is already
	/// there or there is no README.
	/// </summary>
	private static bool InsertPhrase(string path, string phrase)
	{
		if (!File.Exists(path))
		{
			return false;
		}

		var (text, bom) = Read(path);
		if (text.Contains(phrase, StringComparison.Ordinal))
		{
			return false;
		}

		var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
		var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
		var title = lines.FindIndex(line => line.StartsWith("# ", StringComparison.Ordinal));

		var insertAt = 0;
		if (title >= 0)
		{
			insertAt = title + 1;
			while (insertAt < lines.Count && IsBlankOrBadge(lines[insertAt]))
			{
				insertAt++;
			}
		}

		var block = new List<string>();
		if (insertAt > 0 && lines[insertAt - 1].Trim().Length > 0)
		{
			block.Add(string.Empty);
		}

		block.Add(phrase + ".");
		if (insertAt < lines.Count)
		{
			block.Add(string.Empty);
		}

		lines.InsertRange(insertAt, block);
		Write(path, string.Join(newline, lines), bom);
		return true;
	}

	private static bool IsBlankOrBadge(string line)
	{
		var trimmed = line.Trim();
		return trimmed.Length == 0
			|| trimmed.StartsWith("[![", StringComparison.Ordinal)
			|| trimmed.StartsWith("![", StringComparison.Ordinal)
			|| trimmed.StartsWith("<img", StringComparison.OrdinalIgnoreCase)
			|| trimmed.StartsWith("<a ", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Replaces every plain modern version in the file that is not <paramref name="current"/>.
	/// </summary>
	private static bool RewriteMentions(string path, string current)
	{
		if (!File.Exists(path))
		{
			return false;
		}

		var (text, bom) = Read(path);
		var updated = ProseRegex().Replace(text, match =>
			match.Groups["kind"].Success || Normalise(match.Groups["version"].Value) == current
				? match.Value
				: ".NET " + current);

		updated = MonikerRegex().Replace(updated, match =>
			match.Groups["version"].Value == current ? match.Value : "net" + current);

		if (updated == text)
		{
			return false;
		}

		Write(path, updated, bom);
		return true;
	}

	private static string Normalise(string version) => version.Contains('.', StringComparison.Ordinal) ? version : version + ".0";

	private static (string Text, bool Bom) Read(string path)
	{
		var bytes = File.ReadAllBytes(path);
		var bom = bytes is [0xEF, 0xBB, 0xBF, ..];
		return (Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
	}

	private static void Write(string path, string text, bool bom)
		=> File.WriteAllText(path, text, new UTF8Encoding(bom));

	[GeneratedRegex(@"^\.NET (?<version>\d+\.\d+)$")]
	private static partial Regex CurrentVersionRegex();

	[GeneratedRegex(@"\s<[^>]+>$")]
	private static partial Regex PropertySuffixRegex();

	[GeneratedRegex(@"\.NET\s+(?:(?<kind>Framework|Standard|Core)\s+)?(?<version>\d+(?:\.\d+)?)(?!\.\d)")]
	private static partial Regex ProseRegex();

	[GeneratedRegex(@"(?<![\w.-])net(?<version>\d{1,2}\.\d)(?![\w-]|\.\d)")]
	private static partial Regex MonikerRegex();
}
