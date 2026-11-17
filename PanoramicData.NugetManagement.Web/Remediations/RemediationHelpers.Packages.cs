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
	/// Adds a PackageVersion entry to Directory.Packages.props.
	/// </summary>
	public static void AddPackageVersion(
		string localPath,
		string packageName,
		string packageVersion,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		const string relativePath = "Directory.Packages.props";
		var fullPath = ResolvePath(localPath, relativePath);
		if (!File.Exists(fullPath))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} does not exist — cannot add package version.");
			return;
		}

		var xml = File.ReadAllText(fullPath);
		if (xml.Contains(packageName, StringComparison.OrdinalIgnoreCase))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} already references {packageName} — skipping.");
			return;
		}

		try
		{
			var doc = XDocument.Parse(xml);
			var itemGroup = ItemGroupHolding(doc, "PackageVersion");

			itemGroup.Add(new XElement("PackageVersion",
				new XAttribute("Include", packageName),
				new XAttribute("Version", packageVersion)));

			doc.Save(fullPath);
			applied.Add(relativePath);
			onOutput?.Invoke($"✅ [{result.RuleId}] Added <PackageVersion Include=\"{packageName}\" Version=\"{packageVersion}\" /> to {relativePath}");
		}
		catch (Exception ex)
		{
			onOutput?.Invoke($"❌ [{result.RuleId}] Failed to modify {relativePath}: {ex.Message}");
		}
	}

	/// <summary>
	/// Updates explicit package version declarations in repository files.
	/// </summary>
	public static void UpdatePackageVersions(
		string localPath,
		string[] updates,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		var groupedUpdates = updates
			.Select(ParsePackageVersionUpdate)
			.Where(update => update is not null)
			.Select(update => update!)
			.GroupBy(update => update.RelativePath, StringComparer.OrdinalIgnoreCase);

		foreach (var fileUpdates in groupedUpdates)
		{
			ApplyUpdatesToFile(localPath, fileUpdates.Key, [.. fileUpdates], result, applied, onOutput);
		}
	}

	private static XElement ItemGroupHolding(XDocument doc, string childName)
	{
		var itemGroup = doc.Root?.Elements("ItemGroup")
			.FirstOrDefault(group => group.Elements(childName).Any());

		if (itemGroup is null)
		{
			itemGroup = new XElement("ItemGroup");
			doc.Root?.Add(itemGroup);
		}

		return itemGroup;
	}

	private static void ApplyUpdatesToFile(
		string localPath,
		string relativePath,
		List<PackageVersionUpdate> fileUpdates,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		var fullPath = ResolvePath(localPath, relativePath);
		if (!File.Exists(fullPath))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} does not exist — cannot update package versions.");
			return;
		}

		try
		{
			var doc = XDocument.Load(fullPath, LoadOptions.PreserveWhitespace);
			var changed = UpdateAll(doc, fileUpdates);

			if (!changed)
			{
				onOutput?.Invoke($"⏭️ [{result.RuleId}] No matching package version entries found in {relativePath} — skipping.");
				return;
			}

			doc.Save(fullPath);
			AddOnce(applied, relativePath);

			onOutput?.Invoke($"✅ [{result.RuleId}] Updated package versions in {relativePath}");
		}
		catch (Exception ex)
		{
			onOutput?.Invoke($"❌ [{result.RuleId}] Failed to modify {relativePath}: {ex.Message}");
		}
	}

	/// <summary>
	/// Removes Version attributes from PackageReference elements in .csproj files,
	/// first migrating the versions to Directory.Packages.props as PackageVersion entries.
	/// </summary>
	public static void RemovePackageReferenceVersions(
		string localPath,
		string[] projectPaths,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		// Phase 1: collect all package name + version pairs from all projects
		var packageVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		foreach (var projectPath in projectPaths)
		{
			CollectPackageVersions(localPath, projectPath, packageVersions, result, onOutput);
		}

		// Phase 2: add PackageVersion entries to Directory.Packages.props
		if (packageVersions.Count > 0)
		{
			MigrateVersionsToDirectoryPackagesProps(localPath, packageVersions, result, applied, onOutput);
		}

		// Phase 3: remove Version attributes/elements from csproj files
		foreach (var projectPath in projectPaths)
		{
			StripVersions(localPath, projectPath, result, applied, onOutput);
		}
	}

	private static bool UpdateAll(XDocument doc, List<PackageVersionUpdate> fileUpdates)
	{
		var changed = false;
		foreach (var update in fileUpdates)
		{
			changed |= UpdatePackageVersion(doc, update);
		}

		return changed;
	}

	private static void AddOnce(List<string> applied, string relativePath)
	{
		if (!applied.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
		{
			applied.Add(relativePath);
		}
	}

	private static void CollectPackageVersions(
		string localPath,
		string projectPath,
		Dictionary<string, string> packageVersions,
		RuleResult result,
		Action<string>? onOutput)
	{
		var fullPath = ResolvePath(localPath, projectPath);
		if (!File.Exists(fullPath))
		{
			return;
		}

		try
		{
			var doc = XDocument.Load(fullPath);
			foreach (var pkgRef in doc.Descendants("PackageReference"))
			{
				RecordVersion(pkgRef, packageVersions);
			}
		}
		catch (Exception ex)
		{
			onOutput?.Invoke($"❌ [{result.RuleId}] Failed to read {projectPath}: {ex.Message}");
		}
	}

	private static void RecordVersion(XElement pkgRef, Dictionary<string, string> packageVersions)
	{
		var name = pkgRef.Attribute("Include")?.Value;
		if (string.IsNullOrEmpty(name))
		{
			return;
		}

		// Version from attribute, then from child element
		var version = pkgRef.Attribute("Version")?.Value ?? pkgRef.Element("Version")?.Value;

		if (!string.IsNullOrEmpty(version))
		{
			packageVersions.TryAdd(name, version);
		}
	}

	private static void StripVersions(
		string localPath,
		string projectPath,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		var fullPath = ResolvePath(localPath, projectPath);
		if (!File.Exists(fullPath))
		{
			return;
		}

		try
		{
			var doc = XDocument.Load(fullPath);
			var changed = false;
			foreach (var pkgRef in doc.Descendants("PackageReference"))
			{
				changed |= RemoveIfPresent(pkgRef.Attribute("Version"));

				// Also remove <Version> child element
				changed |= RemoveIfPresent(pkgRef.Element("Version"));
			}

			if (changed)
			{
				doc.Save(fullPath);
				applied.Add(projectPath);
				onOutput?.Invoke($"✅ [{result.RuleId}] Removed Version attributes from PackageReference in {projectPath}");
			}
		}
		catch (Exception ex)
		{
			onOutput?.Invoke($"❌ [{result.RuleId}] Failed to modify {projectPath}: {ex.Message}");
		}
	}

	private static bool RemoveIfPresent(XAttribute? attribute)
	{
		attribute?.Remove();
		return attribute is not null;
	}

	private static bool RemoveIfPresent(XElement? element)
	{
		element?.Remove();
		return element is not null;
	}

	/// <summary>
	/// Adds PackageVersion entries to Directory.Packages.props for the given package name/version pairs.
	/// Skips packages that already have a PackageVersion entry.
	/// </summary>
	private static void MigrateVersionsToDirectoryPackagesProps(
		string localPath,
		Dictionary<string, string> packageVersions,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		const string relativePath = "Directory.Packages.props";
		var fullPath = ResolvePath(localPath, relativePath);
		if (!File.Exists(fullPath))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} does not exist — cannot migrate package versions.");
			return;
		}

		try
		{
			var doc = XDocument.Load(fullPath);

			var added = AddMissingPackageVersions(ItemGroupHolding(doc, "PackageVersion"), packageVersions);

			if (added > 0)
			{
				doc.Save(fullPath);
				applied.Add(relativePath);
				onOutput?.Invoke($"✅ [{result.RuleId}] Added {added} PackageVersion entries to {relativePath}");
			}
		}
		catch (Exception ex)
		{
			onOutput?.Invoke($"❌ [{result.RuleId}] Failed to modify {relativePath}: {ex.Message}");
		}
	}

	/// <summary>
	/// Adds file entries to the Solution Items folder in a .slnx file.
	/// </summary>
	public static void AddSlnxFileEntries(
		string localPath,
		string relativePath,
		string[] missingFiles,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		var fullPath = ResolvePath(localPath, relativePath);
		if (!File.Exists(fullPath))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} does not exist — cannot add entries.");
			return;
		}

		try
		{
			var doc = XDocument.Load(fullPath);
			var addedCount = AddMissingFileEntries(SolutionItemsFolder(doc), missingFiles);

			if (addedCount > 0)
			{
				doc.Save(fullPath);
				applied.Add(relativePath);
				onOutput?.Invoke($"✅ [{result.RuleId}] Added {addedCount} file entries to Solution Items in {relativePath}");
			}
			else
			{
				onOutput?.Invoke($"⏭️ [{result.RuleId}] All files already in Solution Items — skipping.");
			}
		}
		catch (Exception ex)
		{
			onOutput?.Invoke($"❌ [{result.RuleId}] Failed to modify {relativePath}: {ex.Message}");
		}
	}

	private static int AddMissingPackageVersions(XElement itemGroup, Dictionary<string, string> packageVersions)
	{
		var added = 0;
		foreach (var (name, version) in packageVersions.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
		{
			// Skip if already present
			var existing = itemGroup.Elements("PackageVersion")
				.Any(pv => string.Equals(pv.Attribute("Include")?.Value, name, StringComparison.OrdinalIgnoreCase));

			if (existing)
			{
				continue;
			}

			itemGroup.Add(new XElement("PackageVersion",
				new XAttribute("Include", name),
				new XAttribute("Version", version)));
			added++;
		}

		return added;
	}

	private static XElement SolutionItemsFolder(XDocument doc)
	{
		var solutionItemsFolder = doc.Root?
			.Elements("Folder")
			.FirstOrDefault(f => f.Attribute("Name")?.Value is { } name
				&& name.Contains("Solution Items", StringComparison.OrdinalIgnoreCase));

		if (solutionItemsFolder is null)
		{
			solutionItemsFolder = new XElement("Folder", new XAttribute("Name", "/Solution Items/"));
			doc.Root?.Add(solutionItemsFolder);
		}

		return solutionItemsFolder;
	}

	private static int AddMissingFileEntries(XElement solutionItemsFolder, string[] missingFiles)
	{
		var addedCount = 0;
		foreach (var missingFile in missingFiles)
		{
			var alreadyExists = solutionItemsFolder.Elements("File")
				.Any(el => string.Equals(el.Attribute("Path")?.Value, missingFile, StringComparison.OrdinalIgnoreCase));
			if (alreadyExists)
			{
				continue;
			}

			solutionItemsFolder.Add(new XElement("File", new XAttribute("Path", missingFile)));
			addedCount++;
		}

		return addedCount;
	}
}
