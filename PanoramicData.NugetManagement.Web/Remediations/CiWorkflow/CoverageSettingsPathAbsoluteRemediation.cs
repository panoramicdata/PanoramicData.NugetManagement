using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Web.Remediations.CiWorkflow;

/// <summary>
/// Makes the relative <c>--coverage-settings</c> paths in a repository's workflows absolute.
/// </summary>
/// <remarks>
/// Re-reads each file as it is now and writes only what the editor changes, so a workflow edited since
/// the assessment is handled correctly and one that no longer needs it is left untouched.
/// </remarks>
public sealed class CoverageSettingsPathAbsoluteRemediation : IRemediation
{
	/// <inheritdoc />
	public string RuleId => "CI-16";

	/// <inheritdoc />
	public bool CanRemediate(RuleResult result)
		=> !result.Passed && result.Advisory is not null && ReadFiles(result).Length > 0;

	/// <inheritdoc />
	public void Apply(string localPath, RuleResult result, List<string> applied, Action<string>? onOutput)
	{
		var root = Path.GetFullPath(localPath);

		foreach (var relativePath in ReadFiles(result))
		{
			var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));

			// The path comes from advisory data; never let it leave the clone.
			if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
			{
				onOutput?.Invoke($"⏭️ [CI-16] {relativePath} is not a file in this repository — skipping.");
				continue;
			}

			var edited = CoverageSettingsPathEditor.MakeAbsolute(File.ReadAllText(fullPath));
			if (edited is null)
			{
				onOutput?.Invoke($"⏭️ [CI-16] {relativePath} has no relative --coverage-settings path to fix — skipping.");
				continue;
			}

			File.WriteAllText(fullPath, edited);
			applied.Add(relativePath);
			onOutput?.Invoke($"✅ [CI-16] Made the --coverage-settings path in {relativePath} absolute");
		}
	}

	private static string[] ReadFiles(RuleResult result)
		=> result.Advisory?.Data.TryGetValue("workflow_files", out var value) == true && value is IEnumerable<string> files
			? [.. files]
			: [];
}
