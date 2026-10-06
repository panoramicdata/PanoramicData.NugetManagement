using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace PanoramicData.NugetManagement.Web.Remediations.CiWorkflow;

/// <summary>
/// Repairs workflow files that do not parse as YAML because their indentation was written with tabs.
/// </summary>
/// <remarks>
/// A file is written only once the repaired text has been shown to parse. CI-14 exists because a
/// remediation once rewrote workflows into something GitHub rejected, so this one never writes on faith:
/// a file that still fails after the repair is left exactly as it was, and stays flagged. The one
/// exception is codeql.yml, which is replaced with the standard template when repair is not enough —
/// a workflow GitHub cannot parse analyses nothing, so there is no working behaviour to lose.
/// </remarks>
public sealed partial class WorkflowYamlRemediation : IRemediation
{
	private const string _codeQlPath = ".github/workflows/codeql.yml";

	/// <inheritdoc />
	public string RuleId => "CI-14";

	/// <inheritdoc />
	public bool CanRemediate(RuleResult result)
		=> !result.Passed && result.Advisory is not null && ReadBrokenFiles(result).Length > 0;

	/// <inheritdoc />
	public void Apply(string localPath, RuleResult result, List<string> applied, Action<string>? onOutput)
	{
		foreach (var relativePath in ReadBrokenFiles(result))
		{
			var fullPath = Path.GetFullPath(Path.Combine(localPath, relativePath));
			var root = Path.GetFullPath(localPath);

			// The path comes from advisory data; never let it leave the clone.
			if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
			{
				onOutput?.Invoke($"⏭️ [CI-14] {relativePath} is not a file in this repository — skipping.");
				continue;
			}

			var original = File.ReadAllText(fullPath);
			var repaired = ReplaceLeadingTabs(original);

			if (repaired != original && Parses(repaired))
			{
				File.WriteAllText(fullPath, repaired);
				applied.Add(relativePath);
				onOutput?.Invoke($"✅ [CI-14] Replaced indentation tabs with spaces in {relativePath}");
				continue;
			}

			if (relativePath.Equals(_codeQlPath, StringComparison.OrdinalIgnoreCase) && Parses(Standards.CodeQlWorkflowContent))
			{
				File.WriteAllText(fullPath, Standards.CodeQlWorkflowContent);
				applied.Add(relativePath);
				onOutput?.Invoke($"✅ [CI-14] {relativePath} could not be repaired, so it was replaced with the standard CodeQL workflow");
				continue;
			}

			onOutput?.Invoke($"⚠️ [CI-14] {relativePath} still does not parse after repair and was left unchanged — it needs a person or Fix with AI.");
		}
	}

	/// <summary>
	/// Replaces each tab in a line's leading whitespace with four spaces — the width the templates use —
	/// and leaves tabs after the first character alone, since those are values rather than indentation.
	/// </summary>
	internal static string ReplaceLeadingTabs(string content)
		=> LeadingWhitespacePattern().Replace(content, match => match.Value.Replace("\t", "    "));

	private static bool Parses(string content)
	{
		try
		{
			new YamlStream().Load(new StringReader(content));
			return true;
		}
		catch (YamlException)
		{
			return false;
		}
	}

	private static string[] ReadBrokenFiles(RuleResult result)
		=> result.Advisory?.Data.TryGetValue("broken_files", out var value) == true
			? value switch
			{
				IEnumerable<string> strings => [.. strings],
				_ => []
			}
			: [];

	[GeneratedRegex(@"^[ \t]+", RegexOptions.Multiline)]
	private static partial Regex LeadingWhitespacePattern();
}
