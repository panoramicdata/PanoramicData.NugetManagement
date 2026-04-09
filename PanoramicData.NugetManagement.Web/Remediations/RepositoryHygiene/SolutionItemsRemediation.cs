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
	public override bool CanRemediate(RuleResult result)
	{
		if (result.Passed || result.Advisory is null)
		{
			return false;
		}

		var data = result.Advisory.Data;
		if (data.TryGetValue("remediation_type", out var rtObj) &&
			rtObj is string rt &&
			string.Equals(rt, "add_slnx_file_entries", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (data.TryGetValue("expected_extension", out var extObj) &&
			extObj is string ext &&
			string.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		return data.TryGetValue("file", out var fileObj) &&
			fileObj is string file &&
			file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
	}

	/// <inheritdoc />
	public override void Apply(string localPath, RuleResult result, List<string> applied, Action<string>? onOutput)
	{
		if (result.Passed || result.Advisory is null)
		{
			return;
		}

		if (!RemediationHelpers.EnsureSlnxFromLegacySolution(localPath, result, applied, onOutput))
		{
			return;
		}

		var data = result.Advisory.Data;
		string? slnxRelativePath = null;

		if (data.TryGetValue("file", out var fObj) && fObj is string candidate &&
			candidate.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
		{
			slnxRelativePath = candidate;
		}

		slnxRelativePath ??= Directory.GetFiles(localPath, "*.slnx", SearchOption.TopDirectoryOnly)
			.Select(Path.GetFileName)
			.FirstOrDefault();

		if (string.IsNullOrWhiteSpace(slnxRelativePath))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] No .slnx file found — cannot add entries.");
			return;
		}

		ApplyCore(localPath, result, data, "add_slnx_file_entries", applied, onOutput);
	}

	/// <inheritdoc />
	protected override void ApplyCore(
		string localPath,
		RuleResult result,
		Dictionary<string, object> data,
		string remediationType,
		List<string> applied,
		Action<string>? onOutput)
	{
		string? slnxRelativePath = null;
		if (data.TryGetValue("file", out var fObj) && fObj is string f &&
			f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
		{
			slnxRelativePath = f;
		}

		slnxRelativePath ??= Directory.GetFiles(localPath, "*.slnx", SearchOption.TopDirectoryOnly)
			.Select(Path.GetFileName)
			.FirstOrDefault();

		if (string.IsNullOrWhiteSpace(slnxRelativePath))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] No .slnx file found — cannot add entries.");
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
