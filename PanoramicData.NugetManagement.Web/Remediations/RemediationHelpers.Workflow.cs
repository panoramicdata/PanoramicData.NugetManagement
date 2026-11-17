using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.FileSystemGlobbing;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations;

internal static partial class RemediationHelpers
{
	/// <summary>
	/// Sets <c>fetch-depth: 0</c> on every <c>actions/checkout</c> step in a workflow, adding a
	/// <c>with:</c> block where the step has none. Nerdbank.GitVersioning needs the full history, and
	/// a shallow clone silently produces a wrong version rather than failing.
	/// </summary>
	/// <param name="localPath">The root path of the cloned repository.</param>
	/// <param name="relativePath">The workflow file, relative to the repository root.</param>
	/// <param name="result">The failed rule result being remediated.</param>
	/// <param name="applied">List to append successfully modified file paths to.</param>
	/// <param name="onOutput">Optional callback for progress messages.</param>
	public static void EnsureCheckoutFetchDepth(
		string localPath,
		string relativePath,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		var fullPath = ResolvePath(localPath, relativePath);
		if (!File.Exists(fullPath))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} does not exist — cannot set fetch-depth.");
			return;
		}

		var original = File.ReadAllText(fullPath);
		var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
		var lines = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

		var changed = SetFetchDepthOnAllSteps(lines);

		if (!changed)
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] No actions/checkout step to change in {relativePath} — skipping.");
			return;
		}

		File.WriteAllText(fullPath, string.Join(newline, lines));
		applied.Add(relativePath);
		onOutput?.Invoke($"✅ [{result.RuleId}] Set fetch-depth: 0 on actions/checkout in {relativePath}");
	}

	/// <summary>
	/// Sets <c>fetch-depth: 0</c> on the single checkout step whose <c>uses:</c> line is at
	/// <paramref name="usesLineIndex"/>, mutating <paramref name="lines"/> in place.
	/// </summary>
	/// <returns>True if any line was changed or inserted.</returns>
	private static bool SetFetchDepthForStep(List<string> lines, int usesLineIndex, int keyIndent)
	{
		var end = FindStepEnd(lines, usesLineIndex, keyIndent);
		var withIndex = FindWithLine(lines, usesLineIndex, end, keyIndent);

		if (withIndex < 0)
		{
			lines.Insert(usesLineIndex + 1, $"{new string(' ', keyIndent)}with:");
			lines.Insert(usesLineIndex + 2, $"{new string(' ', keyIndent + 2)}fetch-depth: 0");
			return true;
		}

		var existing = UpdateExistingFetchDepth(lines, withIndex, end, keyIndent);
		if (existing is not null)
		{
			return existing.Value;
		}

		lines.Insert(withIndex + 1, $"{new string(' ', EntryIndent(lines, withIndex, end, keyIndent))}fetch-depth: 0");
		return true;
	}

	private static bool SetFetchDepthOnAllSteps(List<string> lines)
	{
		var changed = false;

		// Walk backwards so the line indices of steps not yet visited stay valid as lines are inserted.
		for (var i = lines.Count - 1; i >= 0; i--)
		{
			var match = CheckoutUsesLine().Match(lines[i]);
			if (!match.Success)
			{
				continue;
			}

			// The indent of the step's keys: for "- uses: ..." that is the column of "uses", so the
			// "with:" we add lines up with it rather than with the dash.
			var keyIndent = match.Groups["lead"].Value.Length + match.Groups["dash"].Value.Length;
			changed |= SetFetchDepthForStep(lines, i, keyIndent);
		}

		return changed;
	}

	private static int FindStepEnd(List<string> lines, int usesLineIndex, int keyIndent)
	{
		// The step runs until a line that is neither blank nor indented past the step's keys.
		var end = usesLineIndex + 1;
		while (end < lines.Count && (lines[end].Trim().Length == 0 || IndentOf(lines[end]) >= keyIndent))
		{
			// A sibling list item at the step's own indent starts the next step.
			if (IndentOf(lines[end]) == keyIndent && lines[end].TrimStart().StartsWith('-'))
			{
				break;
			}

			end++;
		}

		return end;
	}

	private static int FindWithLine(List<string> lines, int usesLineIndex, int end, int keyIndent)
	{
		for (var i = usesLineIndex + 1; i < end; i++)
		{
			if (IndentOf(lines[i]) == keyIndent && lines[i].Trim() == "with:")
			{
				return i;
			}
		}

		return -1;
	}

	private static int EntryIndent(List<string> lines, int withIndex, int end, int keyIndent)
	{
		// Reuse whatever indent the existing "with:" entries already use.
		for (var i = withIndex + 1; i < end; i++)
		{
			if (lines[i].Trim().Length == 0)
			{
				continue;
			}

			return IndentOf(lines[i]) <= keyIndent ? keyIndent + 2 : IndentOf(lines[i]);
		}

		return keyIndent + 2;
	}

	/// <returns>Null when there is no fetch-depth line, otherwise whether it had to change.</returns>
	private static bool? UpdateExistingFetchDepth(List<string> lines, int withIndex, int end, int keyIndent)
	{
		for (var i = withIndex + 1; i < end; i++)
		{
			if (IndentOf(lines[i]) <= keyIndent && lines[i].Trim().Length > 0)
			{
				break;
			}

			var existing = FetchDepthLine().Match(lines[i]);
			if (!existing.Success)
			{
				continue;
			}

			if (existing.Groups["value"].Value == "0")
			{
				return false;
			}

			lines[i] = $"{new string(' ', IndentOf(lines[i]))}fetch-depth: 0";
			return true;
		}

		return null;
	}

	private static int IndentOf(string line) => line.Length - line.TrimStart(' ').Length;

	[GeneratedRegex(@"^(?<lead>\s*)(?<dash>-\s+)?uses:\s*actions/checkout@", RegexOptions.IgnoreCase)]
	private static partial Regex CheckoutUsesLine();

	[GeneratedRegex(@"^\s*fetch-depth:\s*(?<value>\S+)\s*$", RegexOptions.IgnoreCase)]
	private static partial Regex FetchDepthLine();
}
