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
	/// Ensures a root .slnx exists by migrating a root .sln when needed.
	/// On success, deletes the migrated .sln file.
	/// </summary>
	public static bool EnsureSlnxFromLegacySolution(
		string localPath,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		var existingSlnx = Directory.GetFiles(localPath, "*.slnx", SearchOption.TopDirectoryOnly)
			.FirstOrDefault();
		if (existingSlnx is not null)
		{
			return true;
		}

		var slnCandidates = Directory.GetFiles(localPath, "*.sln", SearchOption.TopDirectoryOnly)
			.ToArray();
		var slnPath = PickLegacySolution(localPath, slnCandidates);
		if (slnPath is null)
		{
			Say(onOutput, $"⏭️ [{result.RuleId}] No root .sln file found to migrate.");
			return false;
		}

		if (slnCandidates.Length > 1)
		{
			Say(onOutput, $"ℹ️ [{result.RuleId}] Multiple .sln files found; selected {Path.GetFileName(slnPath)} for migration.");
		}

		var slnFileName = Path.GetFileName(slnPath);
		Say(onOutput, $"▶ [{result.RuleId}] Running: dotnet sln {slnFileName} migrate");

		try
		{
			return RunSlnMigrate(localPath, slnFileName, result, onOutput)
				&& CompleteMigration(slnPath, applied, result, onOutput);
		}
		catch (Exception ex)
		{
			Say(onOutput, $"❌ [{result.RuleId}] Failed to migrate {slnFileName}: {ex.Message}");
			return false;
		}
	}

	private static string? PickLegacySolution(string localPath, string[] slnCandidates)
	{
		var repoName = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		return slnCandidates
			.FirstOrDefault(path => string.Equals(
				Path.GetFileNameWithoutExtension(path),
				repoName,
				StringComparison.OrdinalIgnoreCase))
			?? slnCandidates.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
	}

	private static bool RunSlnMigrate(string localPath, string slnFileName, RuleResult result, Action<string>? onOutput)
	{
		using var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = "dotnet",
				ArgumentList = { "sln", slnFileName, "migrate" },
				WorkingDirectory = localPath,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true
			}
		};

		process.Start();
		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		process.WaitForExit();

		if (!string.IsNullOrWhiteSpace(output))
		{
			Say(onOutput, output.Trim());
		}

		if (process.ExitCode == 0)
		{
			return true;
		}

		if (!string.IsNullOrWhiteSpace(error))
		{
			Say(onOutput, error.Trim());
		}

		Say(onOutput, $"❌ [{result.RuleId}] Migration command failed with exit code {process.ExitCode}.");
		return false;
	}

	private static bool CompleteMigration(string slnPath, List<string> applied, RuleResult result, Action<string>? onOutput)
	{
		var slnxPath = Path.ChangeExtension(slnPath, ".slnx");
		if (!File.Exists(slnxPath))
		{
			Say(onOutput, $"❌ [{result.RuleId}] Migration completed but {Path.GetFileName(slnxPath)} was not found.");
			return false;
		}

		File.Delete(slnPath);

		var slnxRelative = Path.GetFileName(slnxPath);
		AddOnce(applied, slnxRelative);
		AddOnce(applied, Path.GetFileName(slnPath));

		var slnFileName = Path.GetFileName(slnPath);
		Say(onOutput, $"✅ [{result.RuleId}] Migrated {slnFileName} to {slnxRelative} and deleted {slnFileName}.");
		return true;
	}

	private static bool UpdatePackageVersion(XDocument doc, PackageVersionUpdate update)
	{
		var elementName = update.VersionKind.StartsWith("PackageVersion", StringComparison.Ordinal)
			? "PackageVersion"
			: "PackageReference";

		var elements = doc.Descendants(elementName)
			.Where(element => string.Equals(
				element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value,
				update.PackageId,
				StringComparison.OrdinalIgnoreCase))
			.ToList();

		if (elements.Count == 0)
		{
			return false;
		}

		var asAttribute = update.VersionKind.EndsWith("Attribute", StringComparison.Ordinal);
		var changed = false;
		foreach (var element in elements)
		{
			changed |= asAttribute
				? SetVersionAttribute(element, update.LatestVersion)
				: SetVersionElement(element, update.LatestVersion);
		}

		return changed;
	}

	private static bool SetVersionAttribute(XElement element, string latestVersion)
	{
		var versionAttribute = element.Attribute("Version");
		if (versionAttribute is null)
		{
			element.SetAttributeValue("Version", latestVersion);
			return true;
		}

		if (string.Equals(versionAttribute.Value, latestVersion, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		versionAttribute.Value = latestVersion;
		return true;
	}

	private static bool SetVersionElement(XElement element, string latestVersion)
	{
		var versionElement = element.Element("Version");
		if (versionElement is null)
		{
			element.Add(new XElement("Version", latestVersion));
			return true;
		}

		if (string.Equals(versionElement.Value, latestVersion, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		versionElement.Value = latestVersion;
		return true;
	}

	private static PackageVersionUpdate? ParsePackageVersionUpdate(string value)
	{
		var parts = value.Split('|', 5, StringSplitOptions.None);
		return parts.Length == 5
			? new PackageVersionUpdate(parts[0], parts[1], parts[2], parts[3], parts[4])
			: null;
	}

	private sealed record PackageVersionUpdate(
		string RelativePath,
		string PackageId,
		string VersionKind,
		string CurrentVersion,
		string LatestVersion);
}
