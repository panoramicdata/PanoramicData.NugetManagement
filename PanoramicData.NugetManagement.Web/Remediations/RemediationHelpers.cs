using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.FileSystemGlobbing;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations;

/// <summary>
/// Shared helper methods used by multiple remediation implementations.
/// </summary>
internal static partial class RemediationHelpers
{
	/// <summary>
	/// Creates a file from a template, creating directories as needed.
	/// </summary>
	public static void CreateFile(
		string localPath,
		string relativePath,
		string content,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		var fullPath = ResolvePath(localPath, relativePath);
		if (File.Exists(fullPath))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} already exists — skipping.");
			return;
		}

		EnsureDirectory(fullPath);
		File.WriteAllText(fullPath, content);
		applied.Add(relativePath);
		onOutput?.Invoke($"✅ [{result.RuleId}] Created {relativePath}");
	}

	/// <summary>
	/// Deletes a file from the repository.
	/// </summary>
	public static void DeleteFile(
		string localPath,
		string relativePath,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		var fullPath = ResolvePath(localPath, relativePath);
		if (!File.Exists(fullPath))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} does not exist — skipping.");
			return;
		}

		File.Delete(fullPath);
		applied.Add(relativePath);
		onOutput?.Invoke($"✅ [{result.RuleId}] Deleted {relativePath}");
	}

	/// <summary>
	/// Ensures an XML property exists in a .props or .csproj file.
	/// Creates the file if it doesn't exist.
	/// </summary>
	public static void EnsureXmlProperty(
		string localPath,
		string relativePath,
		string propertyName,
		string propertyValue,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		var fullPath = ResolvePath(localPath, relativePath);
		if (!File.Exists(fullPath))
		{
			EnsureDirectory(fullPath);
			var content = $"""
                <Project>
                  <PropertyGroup>
                    <{propertyName}>{propertyValue}</{propertyName}>
                  </PropertyGroup>
                </Project>
                """;
			File.WriteAllText(fullPath, content);
			applied.Add(relativePath);
			onOutput?.Invoke($"✅ [{result.RuleId}] Created {relativePath} with <{propertyName}>{propertyValue}</{propertyName}>");
			return;
		}

		var xml = File.ReadAllText(fullPath);
		if (xml.Contains($"<{propertyName}>", StringComparison.OrdinalIgnoreCase))
		{
			// Property tag exists — check if it already has the correct value
			if (xml.Contains($"<{propertyName}>{propertyValue}</{propertyName}>", StringComparison.OrdinalIgnoreCase))
			{
				onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} already has <{propertyName}>{propertyValue}</{propertyName}> — skipping.");
				return;
			}

			// Property exists but with a different value — update it
			try
			{
				var doc = XDocument.Parse(xml);
				var existing = doc.Descendants(propertyName).FirstOrDefault();
				if (existing is not null)
				{
					existing.Value = propertyValue;
					doc.Save(fullPath);
					applied.Add(relativePath);
					onOutput?.Invoke($"✅ [{result.RuleId}] Updated <{propertyName}> to {propertyValue} in {relativePath}");
				}
			}
			catch (Exception ex)
			{
				onOutput?.Invoke($"❌ [{result.RuleId}] Failed to update {relativePath}: {ex.Message}");
			}

			return;
		}

		try
		{
			var doc = XDocument.Parse(xml);
			var propertyGroup = doc.Root?.Elements("PropertyGroup").FirstOrDefault();
			if (propertyGroup is null)
			{
				propertyGroup = new XElement("PropertyGroup");
				doc.Root?.AddFirst(propertyGroup);
			}

			propertyGroup.Add(new XElement(propertyName, propertyValue));
			doc.Save(fullPath);
			applied.Add(relativePath);
			onOutput?.Invoke($"✅ [{result.RuleId}] Added <{propertyName}>{propertyValue}</{propertyName}> to {relativePath}");
		}
		catch (Exception ex)
		{
			onOutput?.Invoke($"❌ [{result.RuleId}] Failed to modify {relativePath}: {ex.Message}");
		}
	}

	/// <summary>
	/// Ensures a PackageReference exists in the specified project.
	/// </summary>
	public static void EnsurePackageReference(
		string localPath,
		string relativePath,
		string packageName,
		string? packageVersion,
		string? privateAssets,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		var fullPath = ResolvePath(localPath, relativePath);
		if (!File.Exists(fullPath))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} does not exist — cannot add PackageReference.");
			return;
		}

		try
		{
			var doc = XDocument.Load(fullPath, LoadOptions.PreserveWhitespace);
			var existing = doc.Descendants("PackageReference")
				.FirstOrDefault(element => string.Equals(
					element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value,
					packageName,
					StringComparison.OrdinalIgnoreCase));

			if (existing is not null)
			{
				onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} already references {packageName} — skipping.");
				return;
			}

			var itemGroup = doc.Root?.Elements("ItemGroup")
				.FirstOrDefault(group => group.Elements("PackageReference").Any());

			if (itemGroup is null)
			{
				itemGroup = new XElement("ItemGroup");
				doc.Root?.Add(itemGroup);
			}

			var packageReference = new XElement("PackageReference", new XAttribute("Include", packageName));
			if (!string.IsNullOrWhiteSpace(packageVersion))
			{
				packageReference.SetAttributeValue("Version", packageVersion);
			}

			if (!string.IsNullOrWhiteSpace(privateAssets))
			{
				packageReference.SetAttributeValue("PrivateAssets", privateAssets);
			}

			itemGroup.Add(packageReference);
			doc.Save(fullPath);
			applied.Add(relativePath);
			onOutput?.Invoke($"✅ [{result.RuleId}] Added <PackageReference Include=\"{packageName}\" /> to {relativePath}");
		}
		catch (Exception ex)
		{
			onOutput?.Invoke($"❌ [{result.RuleId}] Failed to modify {relativePath}: {ex.Message}");
		}
	}

	/// <summary>
	/// Appends a line to a file if it doesn't already contain it.
	/// </summary>
	public static void AppendLine(
		string localPath,
		string relativePath,
		string lineContent,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		var fullPath = ResolvePath(localPath, relativePath);
		if (!File.Exists(fullPath))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} does not exist — cannot append.");
			return;
		}

		var content = File.ReadAllText(fullPath);
		if (content.Contains(lineContent, StringComparison.OrdinalIgnoreCase))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} already contains '{lineContent}' — skipping.");
			return;
		}

		if (!content.EndsWith('\n'))
		{
			content += Environment.NewLine;
		}

		content += lineContent + Environment.NewLine;
		File.WriteAllText(fullPath, content);
		applied.Add(relativePath);
		onOutput?.Invoke($"✅ [{result.RuleId}] Appended to {relativePath}");
	}

	/// <summary>
	/// Prepends a line to a file if it doesn't already contain it.
	/// </summary>
	public static void PrependLine(
		string localPath,
		string relativePath,
		string lineContent,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		var fullPath = ResolvePath(localPath, relativePath);
		if (!File.Exists(fullPath))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} does not exist — cannot prepend.");
			return;
		}

		var content = File.ReadAllText(fullPath);
		if (content.Contains(lineContent, StringComparison.OrdinalIgnoreCase))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} already contains the line — skipping.");
			return;
		}

		content = lineContent + Environment.NewLine + Environment.NewLine + content;
		File.WriteAllText(fullPath, content);
		applied.Add(relativePath);
		onOutput?.Invoke($"✅ [{result.RuleId}] Prepended badge to {relativePath}");
	}

	/// <summary>
	/// Replaces the entire content of a file.
	/// </summary>
	public static void ReplaceFileContent(
		string localPath,
		string relativePath,
		string newContent,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		var fullPath = ResolvePath(localPath, relativePath);
		EnsureDirectory(fullPath);
		File.WriteAllText(fullPath, newContent);
		applied.Add(relativePath);
		onOutput?.Invoke($"✅ [{result.RuleId}] Replaced content of {relativePath}");
	}

	/// <summary>
	/// Replaces a text string in a file.
	/// </summary>
	public static void ReplaceInFile(
		string localPath,
		string relativePath,
		string oldText,
		string newText,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		var fullPath = ResolvePath(localPath, relativePath);
		if (!File.Exists(fullPath))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} does not exist — cannot replace.");
			return;
		}

		var content = File.ReadAllText(fullPath);
		if (!content.Contains(oldText, StringComparison.OrdinalIgnoreCase))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} does not contain '{oldText}' — skipping.");
			return;
		}

		content = content.Replace(oldText, newText, StringComparison.OrdinalIgnoreCase);
		File.WriteAllText(fullPath, content);
		applied.Add(relativePath);
		onOutput?.Invoke($"✅ [{result.RuleId}] Replaced '{oldText}' with '{newText}' in {relativePath}");
	}

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
			var itemGroup = doc.Root?.Elements("ItemGroup")
				.FirstOrDefault(ig => ig.Elements("PackageVersion").Any());

			if (itemGroup is null)
			{
				itemGroup = new XElement("ItemGroup");
				doc.Root?.Add(itemGroup);
			}

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
			var fullPath = ResolvePath(localPath, fileUpdates.Key);
			if (!File.Exists(fullPath))
			{
				onOutput?.Invoke($"⏭️ [{result.RuleId}] {fileUpdates.Key} does not exist — cannot update package versions.");
				continue;
			}

			try
			{
				var doc = XDocument.Load(fullPath, LoadOptions.PreserveWhitespace);
				var changed = false;

				foreach (var update in fileUpdates)
				{
					changed |= UpdatePackageVersion(doc, update);
				}

				if (!changed)
				{
					onOutput?.Invoke($"⏭️ [{result.RuleId}] No matching package version entries found in {fileUpdates.Key} — skipping.");
					continue;
				}

				doc.Save(fullPath);
				if (!applied.Contains(fileUpdates.Key, StringComparer.OrdinalIgnoreCase))
				{
					applied.Add(fileUpdates.Key);
				}

				onOutput?.Invoke($"✅ [{result.RuleId}] Updated package versions in {fileUpdates.Key}");
			}
			catch (Exception ex)
			{
				onOutput?.Invoke($"❌ [{result.RuleId}] Failed to modify {fileUpdates.Key}: {ex.Message}");
			}
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
			var fullPath = ResolvePath(localPath, projectPath);
			if (!File.Exists(fullPath))
			{
				continue;
			}

			try
			{
				var doc = XDocument.Load(fullPath);
				foreach (var pkgRef in doc.Descendants("PackageReference"))
				{
					var name = pkgRef.Attribute("Include")?.Value;
					if (string.IsNullOrEmpty(name))
					{
						continue;
					}

					// Version from attribute
					var version = pkgRef.Attribute("Version")?.Value;

					// Version from child element
					version ??= pkgRef.Element("Version")?.Value;

					if (!string.IsNullOrEmpty(version) && !packageVersions.ContainsKey(name))
					{
						packageVersions[name] = version;
					}
				}
			}
			catch (Exception ex)
			{
				onOutput?.Invoke($"❌ [{result.RuleId}] Failed to read {projectPath}: {ex.Message}");
			}
		}

		// Phase 2: add PackageVersion entries to Directory.Packages.props
		if (packageVersions.Count > 0)
		{
			MigrateVersionsToDirectoryPackagesProps(localPath, packageVersions, result, applied, onOutput);
		}

		// Phase 3: remove Version attributes/elements from csproj files
		foreach (var projectPath in projectPaths)
		{
			var fullPath = ResolvePath(localPath, projectPath);
			if (!File.Exists(fullPath))
			{
				continue;
			}

			try
			{
				var doc = XDocument.Load(fullPath);
				var changed = false;
				foreach (var pkgRef in doc.Descendants("PackageReference"))
				{
					var versionAttr = pkgRef.Attribute("Version");
					if (versionAttr is not null)
					{
						versionAttr.Remove();
						changed = true;
					}

					// Also remove <Version> child element
					var versionElement = pkgRef.Element("Version");
					if (versionElement is not null)
					{
						versionElement.Remove();
						changed = true;
					}
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

			// Find or create an ItemGroup for PackageVersion entries
			var itemGroup = doc.Root?.Elements("ItemGroup")
				.FirstOrDefault(ig => ig.Elements("PackageVersion").Any());

			if (itemGroup is null)
			{
				itemGroup = new XElement("ItemGroup");
				doc.Root?.Add(itemGroup);
			}

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
			var solutionItemsFolder = doc.Root?
				.Elements("Folder")
				.FirstOrDefault(f =>
				{
					var name = f.Attribute("Name")?.Value;
					return name is not null &&
						name.Contains("Solution Items", StringComparison.OrdinalIgnoreCase);
				});

			if (solutionItemsFolder is null)
			{
				solutionItemsFolder = new XElement("Folder", new XAttribute("Name", "/Solution Items/"));
				doc.Root?.Add(solutionItemsFolder);
			}

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

	/// <summary>
	/// Resolves a relative path to a full path under the local repo root.
	/// </summary>
	public static string ResolvePath(string localPath, string relativePath)
		=> Path.Combine(localPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

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
		var repoName = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		var slnPath = slnCandidates
			.FirstOrDefault(path => string.Equals(
				Path.GetFileNameWithoutExtension(path),
				repoName,
				StringComparison.OrdinalIgnoreCase))
			?? slnCandidates.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
		if (slnPath is null)
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] No root .sln file found to migrate.");
			return false;
		}

		if (slnCandidates.Length > 1)
		{
			onOutput?.Invoke($"ℹ️ [{result.RuleId}] Multiple .sln files found; selected {Path.GetFileName(slnPath)} for migration.");
		}

		var slnFileName = Path.GetFileName(slnPath);
		onOutput?.Invoke($"▶ [{result.RuleId}] Running: dotnet sln {slnFileName} migrate");

		try
		{
			using var process = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = "dotnet",
					Arguments = $"sln \"{slnFileName}\" migrate",
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
				onOutput?.Invoke(output.Trim());
			}

			if (process.ExitCode != 0)
			{
				if (!string.IsNullOrWhiteSpace(error))
				{
					onOutput?.Invoke(error.Trim());
				}

				onOutput?.Invoke($"❌ [{result.RuleId}] Migration command failed with exit code {process.ExitCode}.");
				return false;
			}

			var slnxPath = Path.ChangeExtension(slnPath, ".slnx");
			if (!File.Exists(slnxPath))
			{
				onOutput?.Invoke($"❌ [{result.RuleId}] Migration completed but {Path.GetFileName(slnxPath)} was not found.");
				return false;
			}

			File.Delete(slnPath);

			var slnxRelative = Path.GetFileName(slnxPath);
			if (!applied.Any(path => string.Equals(path, slnxRelative, StringComparison.OrdinalIgnoreCase)))
			{
				applied.Add(slnxRelative);
			}

			var slnRelative = Path.GetFileName(slnPath);
			if (!applied.Any(path => string.Equals(path, slnRelative, StringComparison.OrdinalIgnoreCase)))
			{
				applied.Add(slnRelative);
			}

			onOutput?.Invoke($"✅ [{result.RuleId}] Migrated {slnFileName} to {slnxRelative} and deleted {slnFileName}.");
			return true;
		}
		catch (Exception ex)
		{
			onOutput?.Invoke($"❌ [{result.RuleId}] Failed to migrate {slnFileName}: {ex.Message}");
			return false;
		}
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

		var changed = false;
		foreach (var element in elements)
		{
			if (update.VersionKind.EndsWith("Attribute", StringComparison.Ordinal))
			{
				var versionAttribute = element.Attribute("Version");
				if (versionAttribute is null)
				{
					element.SetAttributeValue("Version", update.LatestVersion);
					changed = true;
					continue;
				}

				if (!string.Equals(versionAttribute.Value, update.LatestVersion, StringComparison.OrdinalIgnoreCase))
				{
					versionAttribute.Value = update.LatestVersion;
					changed = true;
				}
			}
			else
			{
				var versionElement = element.Element("Version");
				if (versionElement is null)
				{
					element.Add(new XElement("Version", update.LatestVersion));
					changed = true;
					continue;
				}

				if (!string.Equals(versionElement.Value, update.LatestVersion, StringComparison.OrdinalIgnoreCase))
				{
					versionElement.Value = update.LatestVersion;
					changed = true;
				}
			}
		}

		return changed;
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

	/// <summary>
	/// Ensures the directory for a file path exists.
	/// </summary>
	public static void EnsureDirectory(string fullPath)
	{
		var dir = Path.GetDirectoryName(fullPath);
		if (dir is not null && !Directory.Exists(dir))
		{
			Directory.CreateDirectory(dir);
		}
	}

	/// <summary>
	/// Adds missing items to a JSON array property in a file.
	/// </summary>
	public static void AddJsonArrayItems(
		string localPath,
		string relativePath,
		string arrayProperty,
		string[] items,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		var fullPath = ResolvePath(localPath, relativePath);
		if (!File.Exists(fullPath))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} does not exist — cannot add items.");
			return;
		}

		try
		{
			var json = File.ReadAllText(fullPath);
			var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
			if (node is not JsonObject root)
			{
				onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} root is not a JSON object.");
				return;
			}

			JsonArray array;
			if (root.TryGetPropertyValue(arrayProperty, out var existing) && existing is JsonArray existingArray)
			{
				array = existingArray;
			}
			else
			{
				array = [];
				root[arrayProperty] = array;
			}

			var currentValues = array
				.Select(n => n?.GetValue<string>())
				.Where(v => v is not null)
				.ToHashSet(StringComparer.Ordinal);

			var added = 0;
			foreach (var item in items)
			{
				if (!currentValues.Contains(item))
				{
					array.Add(JsonValue.Create(item));
					added++;
				}
			}

			if (added > 0)
			{
				var options = new JsonSerializerOptions { WriteIndented = true };
				var updated = root.ToJsonString(options);
				File.WriteAllText(fullPath, updated);
				applied.Add(relativePath);
				onOutput?.Invoke($"✅ [{result.RuleId}] Added {added} item(s) to {arrayProperty} in {relativePath}");
			}
			else
			{
				onOutput?.Invoke($"⏭️ [{result.RuleId}] All items already present in {arrayProperty} — skipping.");
			}
		}
		catch (JsonException ex)
		{
			onOutput?.Invoke($"❌ [{result.RuleId}] Failed to parse {relativePath}: {ex.Message}");
		}
	}

	/// <summary>
	/// Removes all PackageReference elements with the given name from the specified project files.
	/// </summary>
	public static void RemovePackageReference(
		string localPath,
		string packageName,
		string[] projectPaths,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		foreach (var projectPath in projectPaths)
		{
			var fullPath = ResolvePath(localPath, projectPath);
			if (!File.Exists(fullPath))
			{
				onOutput?.Invoke($"⏭️ [{result.RuleId}] {projectPath} does not exist — skipping.");
				continue;
			}

			try
			{
				var doc = XDocument.Load(fullPath, LoadOptions.PreserveWhitespace);
				var toRemove = doc.Descendants("PackageReference")
					.Where(e => string.Equals(
						e.Attribute("Include")?.Value,
						packageName,
						StringComparison.OrdinalIgnoreCase))
					.ToList();

				if (toRemove.Count == 0)
				{
					onOutput?.Invoke($"⏭️ [{result.RuleId}] {projectPath} does not reference {packageName} — skipping.");
					continue;
				}

				foreach (var element in toRemove)
				{
					// Remove the preceding whitespace/newline node too for clean diffs
					if (element.PreviousNode is XText { Value: var ws } && ws.Trim().Length == 0)
					{
						element.PreviousNode.Remove();
					}

					element.Remove();
				}

				// Remove any now-empty ItemGroups
				var emptyGroups = doc.Descendants("ItemGroup")
					.Where(g => !g.HasElements)
					.ToList();
				foreach (var g in emptyGroups)
				{
					if (g.PreviousNode is XText { Value: var ws } && ws.Trim().Length == 0)
					{
						g.PreviousNode.Remove();
					}

					g.Remove();
				}

				doc.Save(fullPath);
				applied.Add(projectPath);
				onOutput?.Invoke($"✅ [{result.RuleId}] Removed {packageName} PackageReference from {projectPath}");
			}
			catch (Exception ex)
			{
				onOutput?.Invoke($"❌ [{result.RuleId}] Failed to modify {projectPath}: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// Removes a PackageVersion pin from Directory.Packages.props.
	/// </summary>
	public static void RemovePackageVersion(
		string localPath,
		string packageName,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		const string relativePath = "Directory.Packages.props";
		var fullPath = ResolvePath(localPath, relativePath);
		if (!File.Exists(fullPath))
		{
			return;
		}

		try
		{
			var doc = XDocument.Load(fullPath, LoadOptions.PreserveWhitespace);
			var toRemove = doc.Descendants("PackageVersion")
				.Where(e => string.Equals(e.Attribute("Include")?.Value, packageName, StringComparison.OrdinalIgnoreCase))
				.ToList();

			if (toRemove.Count == 0)
			{
				return;
			}

			foreach (var element in toRemove)
			{
				// Remove the preceding whitespace/newline node too, for clean diffs
				if (element.PreviousNode is XText { Value: var ws } && ws.Trim().Length == 0)
				{
					element.PreviousNode.Remove();
				}

				element.Remove();
			}

			doc.Save(fullPath);
			applied.Add(relativePath);
			onOutput?.Invoke($"✅ [{result.RuleId}] Removed <PackageVersion Include=\"{packageName}\" /> from {relativePath}");
		}
		catch (Exception ex)
		{
			onOutput?.Invoke($"❌ [{result.RuleId}] Failed to modify {relativePath}: {ex.Message}");
		}
	}

	/// <summary>
	/// Applies an ordered list of regular-expression replacements across every file matching the
	/// given globs.
	/// </summary>
	/// <remarks>
	/// The patterns are applied in order, to each file in turn, because order can be load-bearing:
	/// renaming FluentAssertions before FluentAssertions.Analyzers would rename the analyzer package
	/// by accident and leave its version pin pointing at nothing.
	/// </remarks>
	/// <param name="localPath">The repository root.</param>
	/// <param name="globs">Glob patterns, relative to the root, e.g. <c>**/*.cs</c>.</param>
	/// <param name="patterns">Regular expressions, applied in order.</param>
	/// <param name="replacements">Replacements, one per pattern.</param>
	/// <param name="result">The rule result being remediated.</param>
	/// <param name="applied">Collects the files changed.</param>
	/// <param name="onOutput">Progress reporting.</param>
	public static void ReplaceRegexInFiles(
		string localPath,
		string[] globs,
		string[] patterns,
		string[] replacements,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		if (patterns.Length != replacements.Length)
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {patterns.Length} pattern(s) but {replacements.Length} replacement(s) — skipping.");
			return;
		}

		var root = ResolvePath(localPath, ".");
		if (!Directory.Exists(root))
		{
			return;
		}

		var matcher = new Matcher();
		matcher.AddIncludePatterns(globs);
		// Never rewrite build output or a clone's own git database.
		matcher.AddExcludePatterns(["**/bin/**", "**/obj/**", "**/.git/**"]);

		var matches = matcher.GetResultsInFullPath(root).ToList();
		var changedCount = 0;

		foreach (var file in matches)
		{
			try
			{
				var original = File.ReadAllText(file);
				var updated = original;

				for (var i = 0; i < patterns.Length; i++)
				{
					updated = Regex.Replace(updated, patterns[i], replacements[i]);
				}

				if (updated == original)
				{
					continue;
				}

				File.WriteAllText(file, updated);
				var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
				applied.Add(relative);
				changedCount++;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexParseException)
			{
				onOutput?.Invoke($"❌ [{result.RuleId}] Failed to rewrite {file}: {ex.Message}");
			}
		}

		onOutput?.Invoke(changedCount == 0
			? $"⏭️ [{result.RuleId}] Nothing matched the patterns — no files changed."
			: $"✅ [{result.RuleId}] Rewrote {changedCount} file(s).");
	}

	/// <summary>
	/// Removes a property from a JSON file, addressed by a dotted path such as <c>test.runner</c>.
	/// Does nothing when the property is already absent.
	/// </summary>
	public static void RemoveJsonProperty(
		string localPath,
		string relativePath,
		string propertyPath,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		var fullPath = ResolvePath(localPath, relativePath);
		if (!File.Exists(fullPath))
		{
			return;
		}

		var segments = propertyPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length == 0)
		{
			return;
		}

		try
		{
			var json = File.ReadAllText(fullPath);
			var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
			if (node is not JsonObject root)
			{
				onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} root is not a JSON object.");
				return;
			}

			var target = root;
			foreach (var segment in segments[..^1])
			{
				if (!target.TryGetPropertyValue(segment, out var child) || child is not JsonObject childObject)
				{
					return;
				}

				target = childObject;
			}

			if (!target.Remove(segments[^1]))
			{
				return;
			}

			File.WriteAllText(fullPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
			applied.Add(relativePath);
			onOutput?.Invoke($"✅ [{result.RuleId}] Removed {propertyPath} from {relativePath}");
		}
		catch (JsonException ex)
		{
			onOutput?.Invoke($"❌ [{result.RuleId}] Failed to parse {relativePath}: {ex.Message}");
		}
	}

	/// <summary>
	/// Sets a property in a JSON file, addressed by a dotted path such as <c>test.runner</c>,
	/// creating any intermediate objects.
	/// </summary>
	/// <param name="localPath">The root path of the cloned repository.</param>
	/// <param name="relativePath">The JSON file, relative to the repository root.</param>
	/// <param name="propertyPath">The dotted path of the property to set.</param>
	/// <param name="propertyValue">The value to set, as text.</param>
	/// <param name="result">The failed rule result being remediated.</param>
	/// <param name="applied">List to append successfully modified file paths to.</param>
	/// <param name="onOutput">Optional callback for progress messages.</param>
	/// <param name="createContent">
	/// When supplied, the content to write if the file does not exist. Without it a missing file is
	/// left alone: a rule that only knows the property cannot invent the rest of the document.
	/// </param>
	/// <param name="valueKind">
	/// How to write <paramref name="propertyValue"/>: <c>string</c> (the default), <c>bool</c> or
	/// <c>number</c>. Writing <c>"true"</c> where a schema wants <c>true</c> is not a fix.
	/// </param>
	public static void EnsureJsonProperty(
		string localPath,
		string relativePath,
		string propertyPath,
		string propertyValue,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput,
		string? createContent = null,
		string valueKind = "string")
	{
		var fullPath = ResolvePath(localPath, relativePath);
		if (!File.Exists(fullPath))
		{
			if (createContent is null)
			{
				onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} does not exist — cannot set {propertyPath}.");
				return;
			}

			EnsureDirectory(fullPath);
			File.WriteAllText(fullPath, createContent);
			applied.Add(relativePath);
			onOutput?.Invoke($"✅ [{result.RuleId}] Created {relativePath}");
		}

		var segments = propertyPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length == 0)
		{
			return;
		}

		try
		{
			var json = File.ReadAllText(fullPath);
			var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
			if (node is not JsonObject root)
			{
				onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} root is not a JSON object.");
				return;
			}

			var target = root;
			foreach (var segment in segments[..^1])
			{
				if (target.TryGetPropertyValue(segment, out var child) && child is JsonObject childObject)
				{
					target = childObject;
					continue;
				}

				var created = new JsonObject();
				target[segment] = created;
				target = created;
			}

			var leaf = segments[^1];
			if (target.TryGetPropertyValue(leaf, out var existing) && existing is JsonValue existingValue
				&& string.Equals(existingValue.ToJsonString().Trim('"'), propertyValue, StringComparison.Ordinal))
			{
				onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} already sets {propertyPath} to {propertyValue} — skipping.");
				return;
			}

			target[leaf] = CreateJsonValue(propertyValue, valueKind);

			File.WriteAllText(fullPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
			if (!applied.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
			{
				applied.Add(relativePath);
			}

			onOutput?.Invoke($"✅ [{result.RuleId}] Set {propertyPath} to {propertyValue} in {relativePath}");
		}
		catch (JsonException ex)
		{
			onOutput?.Invoke($"❌ [{result.RuleId}] Failed to parse {relativePath}: {ex.Message}");
		}
	}

	/// <summary>
	/// Builds the JSON value to write for a property, honouring the kind the schema expects.
	/// </summary>
	private static JsonNode? CreateJsonValue(string propertyValue, string valueKind)
		=> valueKind switch
		{
			"bool" => JsonValue.Create(bool.Parse(propertyValue)),
			"number" => JsonValue.Create(decimal.Parse(propertyValue, System.Globalization.CultureInfo.InvariantCulture)),
			_ => JsonValue.Create(propertyValue)
		};

	/// <summary>
	/// Applies regular-expression substitutions to a text file. Used where the edit is a rewrite of
	/// text the rule already located and understands — bumping a pinned action version, for example
	/// — rather than a structural change to the document.
	/// </summary>
	/// <param name="localPath">The root path of the cloned repository.</param>
	/// <param name="relativePath">The file to edit, relative to the repository root.</param>
	/// <param name="patterns">The regular expressions to match.</param>
	/// <param name="replacements">
	/// The replacement text for each pattern, positionally paired with <paramref name="patterns"/>.
	/// </param>
	/// <param name="result">The failed rule result being remediated.</param>
	/// <param name="applied">List to append successfully modified file paths to.</param>
	/// <param name="onOutput">Optional callback for progress messages.</param>
	public static void ReplaceRegexInFile(
		string localPath,
		string relativePath,
		string[] patterns,
		string[] replacements,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		if (patterns.Length != replacements.Length)
		{
			onOutput?.Invoke($"❌ [{result.RuleId}] {patterns.Length} pattern(s) but {replacements.Length} replacement(s) — skipping.");
			return;
		}

		var fullPath = ResolvePath(localPath, relativePath);
		if (!File.Exists(fullPath))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} does not exist — cannot edit.");
			return;
		}

		var original = File.ReadAllText(fullPath);
		var content = original;

		for (var i = 0; i < patterns.Length; i++)
		{
			try
			{
				content = Regex.Replace(content, patterns[i], replacements[i], RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
			}
			catch (RegexParseException ex)
			{
				onOutput?.Invoke($"❌ [{result.RuleId}] Invalid pattern '{patterns[i]}': {ex.Message}");
				return;
			}
			catch (RegexMatchTimeoutException)
			{
				onOutput?.Invoke($"❌ [{result.RuleId}] Pattern '{patterns[i]}' timed out against {relativePath}.");
				return;
			}
		}

		if (content == original)
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] Nothing to change in {relativePath} — skipping.");
			return;
		}

		File.WriteAllText(fullPath, content);
		applied.Add(relativePath);
		onOutput?.Invoke($"✅ [{result.RuleId}] Updated {relativePath}");
	}

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

		var withIndex = -1;
		for (var i = usesLineIndex + 1; i < end; i++)
		{
			if (IndentOf(lines[i]) == keyIndent && lines[i].Trim() == "with:")
			{
				withIndex = i;
				break;
			}
		}

		if (withIndex < 0)
		{
			lines.Insert(usesLineIndex + 1, $"{new string(' ', keyIndent)}with:");
			lines.Insert(usesLineIndex + 2, $"{new string(' ', keyIndent + 2)}fetch-depth: 0");
			return true;
		}

		// Reuse whatever indent the existing "with:" entries already use.
		var entryIndent = keyIndent + 2;
		for (var i = withIndex + 1; i < end; i++)
		{
			if (lines[i].Trim().Length == 0)
			{
				continue;
			}

			if (IndentOf(lines[i]) <= keyIndent)
			{
				break;
			}

			entryIndent = IndentOf(lines[i]);
			break;
		}

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

		lines.Insert(withIndex + 1, $"{new string(' ', entryIndent)}fetch-depth: 0");
		return true;
	}

	private static int IndentOf(string line) => line.Length - line.TrimStart(' ').Length;

	[GeneratedRegex(@"^(?<lead>\s*)(?<dash>-\s+)?uses:\s*actions/checkout@", RegexOptions.IgnoreCase)]
	private static partial Regex CheckoutUsesLine();

	[GeneratedRegex(@"^\s*fetch-depth:\s*(?<value>\S+)\s*$", RegexOptions.IgnoreCase)]
	private static partial Regex FetchDepthLine();
}

