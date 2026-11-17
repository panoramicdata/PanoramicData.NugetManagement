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
		return HasString(data, "remediation_type", value => string.Equals(value, "add_slnx_file_entries", StringComparison.OrdinalIgnoreCase))
			|| HasString(data, "expected_extension", value => string.Equals(value, ".slnx", StringComparison.OrdinalIgnoreCase))
			|| HasString(data, "file", value => value.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));
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
		if (string.IsNullOrWhiteSpace(FindSlnx(localPath, data)))
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
		var slnxRelativePath = FindSlnx(localPath, data);
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

		try
		{
			var missingFiles = FindMissingStandardFiles(localPath, slnxFullPath);
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

	private static bool HasString(Dictionary<string, object> data, string key, Func<string, bool> test)
		=> data.TryGetValue(key, out var value) && value is string text && test(text);

	private static string? FindSlnx(string localPath, Dictionary<string, object> data)
	{
		if (data.TryGetValue("file", out var value) && value is string candidate &&
			candidate.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
		{
			return candidate;
		}

		return Directory.GetFiles(localPath, "*.slnx", SearchOption.TopDirectoryOnly)
			.Select(Path.GetFileName)
			.FirstOrDefault();
	}

	private static string[] FindMissingStandardFiles(string localPath, string slnxFullPath)
	{
		// Determine which standard files actually exist on disk NOW
		var existingStandardFiles = _expectedSolutionItems
			.Where(f => File.Exists(Path.Combine(localPath, f)))
			.ToArray();

		var doc = XDocument.Load(slnxFullPath);
		var solutionItemsFolder = doc.Root?
			.Elements("Folder")
			.FirstOrDefault(f => f.Attribute("Name")?.Value is { } name
				&& name.Contains("Solution Items", StringComparison.OrdinalIgnoreCase));

		var referencedFiles = solutionItemsFolder?
			.Elements("File")
			.Select(f => f.Attribute("Path")?.Value)
			.Where(p => p is not null)
			.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

		return [.. existingStandardFiles.Where(f => !referencedFiles.Contains(f))];
	}
}
