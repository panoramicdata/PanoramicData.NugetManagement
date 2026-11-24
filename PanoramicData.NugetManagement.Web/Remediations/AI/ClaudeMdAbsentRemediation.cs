using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations.AI;

/// <summary>Deletes a CLAUDE.md that this tool generated, and the solution item that pointed at it.</summary>
public sealed class ClaudeMdAbsentRemediation : DataDrivenRemediation
{
	private static readonly Regex _solutionItem = new(
		@"(?m)^[ \t]*<File Path=""CLAUDE\.md"" />\r?\n",
		RegexOptions.None,
		TimeSpan.FromSeconds(2));

	/// <inheritdoc />
	public override string RuleId => "AI-01";

	/// <inheritdoc />
	public override void Apply(string localPath, RuleResult result, List<string> applied, Action<string>? onOutput)
	{
		base.Apply(localPath, result, applied, onOutput);

		if (File.Exists(Path.Combine(localPath, "CLAUDE.md")))
		{
			return;
		}

		foreach (var slnx in Directory.GetFiles(localPath, "*.slnx", SearchOption.TopDirectoryOnly))
		{
			var text = File.ReadAllText(slnx);
			var cleaned = _solutionItem.Replace(text, string.Empty);
			if (cleaned != text)
			{
				File.WriteAllText(slnx, cleaned);
				applied.Add(Path.GetFileName(slnx));
				onOutput?.Invoke($"✅ [{result.RuleId}] Removed the CLAUDE.md solution item from {Path.GetFileName(slnx)}");
			}
		}
	}
}
