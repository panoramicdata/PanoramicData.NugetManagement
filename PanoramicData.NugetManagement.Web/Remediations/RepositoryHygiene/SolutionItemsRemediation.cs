using System.Xml.Linq;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations.RepositoryHygiene;

/// <summary>Adds missing file entries to Solution Items in .slnx.</summary>
public sealed class SolutionItemsRemediation : DataDrivenRemediation
{
	/// <summary>
	/// Standard files that should appear in the Solution Items folder when they exist in the repo.
	/// Must match the list in <see cref="PanoramicData.NugetManagement.Rules.SolutionItemsRule"/>.
	/// </summary>
	private static readonly string[] _expectedSolutionItems =
	[
		".codacy.yml",
		".editorconfig",
		".gitignore",
		"CONTRIBUTING.md",
		"Directory.Build.props",
		"Directory.Packages.props",
		"global.json",
		"LICENSE",
		"Publish.ps1",
		"README.md",
		"SECURITY.md",
		"version.json"
	];

	/// <inheritdoc />
	public override string RuleId => "REPO-05";

	/// <inheritdoc />
	protected override void ApplyCore(
		string localPath,
		RuleResult result,
		Dictionary<string, object> data,
		string remediationType,
		List<string> applied,
		Action<string>? onOutput)
	{
		if (!data.TryGetValue("file", out var fObj) || fObj is not string slnxRelativePath)
		{
			return;
		}

		// Re-scan the local filesystem instead of relying solely on
		// assessment-time advisory data.  Other remediations that ran
		// earlier in this pass may have created new files (e.g.
		// CONTRIBUTING.md, Directory.Build.props) that were not listed
		// in the original missing_files array.
		var slnxFullPath = RemediationHelpers.ResolvePath(localPath, slnxRelativePath);
		if (!File.Exists(slnxFullPath))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {slnxRelativePath} does not exist — cannot add entries.");
			return;
		}

		// Determine which standard files actually exist on disk NOW
		var existingStandardFiles = _expectedSolutionItems
			.Where(f => File.Exists(Path.Combine(localPath, f)))
			.ToArray();

		// Determine which of those are already referenced in Solution Items
		try
		{
			var doc = XDocument.Load(slnxFullPath);
			var solutionItemsFolder = doc.Root?
				.Elements("Folder")
				.FirstOrDefault(f =>
				{
					var name = f.Attribute("Name")?.Value;
					return name is not null &&
						name.Contains("Solution Items", StringComparison.OrdinalIgnoreCase);
				});

			var referencedFiles = solutionItemsFolder?
				.Elements("File")
				.Select(f => f.Attribute("Path")?.Value)
				.Where(p => p is not null)
				.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

			var missingFiles = existingStandardFiles
				.Where(f => !referencedFiles.Contains(f))
				.ToArray();

			if (missingFiles.Length == 0)
			{
				onOutput?.Invoke($"⏭️ [{result.RuleId}] All standard files already in Solution Items.");
				return;
			}

			RemediationHelpers.AddSlnxFileEntries(localPath, slnxRelativePath, missingFiles, result, applied, onOutput);
		}
		catch (Exception ex)
		{
			onOutput?.Invoke($"❌ [{result.RuleId}] Failed to scan {slnxRelativePath}: {ex.Message}");
		}
	}
}
