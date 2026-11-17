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
	/// Moves a project to the target framework. A project that lists TargetFrameworks has that list rewritten,
	/// because MSBuild ignores a singular TargetFramework beside it; any other project gets the singular property.
	/// </summary>
	public static void EnsureTargetFramework(
		string localPath,
		string relativePath,
		string latest,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		var fullPath = ResolvePath(localPath, relativePath);
		XDocument? doc = null;
		if (File.Exists(fullPath) && !TryLoadForEdit(fullPath, relativePath, result, onOutput, out doc))
		{
			return;
		}

		var lists = doc?.Descendants("TargetFrameworks").ToList() ?? [];
		if (doc is null || lists.Count == 0)
		{
			EnsureXmlProperty(localPath, relativePath, "TargetFramework", latest, result, applied, onOutput);
			return;
		}

		if (!RewriteTargetFrameworks(doc, lists, latest))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} already targets {latest} — skipping.");
			return;
		}

		doc.Save(fullPath);
		applied.Add(relativePath);
		onOutput?.Invoke($"✅ [{result.RuleId}] Updated <TargetFrameworks> to include {latest} in {relativePath}");
	}

	private static bool TryLoadForEdit(string fullPath, string relativePath, RuleResult result, Action<string>? onOutput, out XDocument? doc)
	{
		try
		{
			doc = XDocument.Load(fullPath, LoadOptions.PreserveWhitespace);
			return true;
		}
		catch (Exception ex)
		{
			doc = null;
			onOutput?.Invoke($"❌ [{result.RuleId}] Failed to read {relativePath}: {ex.Message}");
			return false;
		}
	}

	private static bool RewriteTargetFrameworks(XDocument doc, List<XElement> lists, string latest)
	{
		var previous = PreviousMoniker(latest);
		var changed = false;

		foreach (var list in lists)
		{
			changed |= RewriteList(list, previous, latest);
		}

		foreach (var single in doc.Descendants("TargetFramework").Where(e => previous is not null && string.Equals(e.Value.Trim(), previous, StringComparison.OrdinalIgnoreCase)))
		{
			single.Value = latest;
			changed = true;
		}

		return changed;
	}

	private static string? PreviousMoniker(string latest)
	{
		var latestMajor = 0;
		var isMajorMoniker = latest.StartsWith("net", StringComparison.Ordinal) && latest.EndsWith(".0", StringComparison.Ordinal)
			&& int.TryParse(latest[3..^2], System.Globalization.CultureInfo.InvariantCulture, out latestMajor);
		return isMajorMoniker ? $"net{latestMajor - 1}.0" : null;
	}

	private static bool RewriteList(XElement list, string? previous, string latest)
	{
		var entries = list.Value
			.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(entry => previous is not null && string.Equals(entry, previous, StringComparison.OrdinalIgnoreCase) ? latest : entry)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (!entries.Contains(latest, StringComparer.OrdinalIgnoreCase))
		{
			entries.Add(latest);
		}

		var joined = string.Join(';', entries);
		if (joined == list.Value)
		{
			return false;
		}

		list.Value = joined;
		return true;
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
			CreatePropertyFile(fullPath, relativePath, propertyName, propertyValue, result, applied, onOutput);
			return;
		}

		var xml = File.ReadAllText(fullPath);
		if (!xml.Contains($"<{propertyName}>", StringComparison.OrdinalIgnoreCase))
		{
			AddProperty(fullPath, relativePath, xml, propertyName, propertyValue, result, applied, onOutput);
			return;
		}

		// Property tag exists — check if it already has the correct value
		if (xml.Contains($"<{propertyName}>{propertyValue}</{propertyName}>", StringComparison.OrdinalIgnoreCase))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} already has <{propertyName}>{propertyValue}</{propertyName}> — skipping.");
			return;
		}

		UpdateProperty(fullPath, relativePath, xml, propertyName, propertyValue, result, applied, onOutput);
	}

	private static void CreatePropertyFile(
		string fullPath,
		string relativePath,
		string propertyName,
		string propertyValue,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
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
	}

	private static void UpdateProperty(
		string fullPath,
		string relativePath,
		string xml,
		string propertyName,
		string propertyValue,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
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
	}

	private static void AddProperty(
		string fullPath,
		string relativePath,
		string xml,
		string propertyName,
		string propertyValue,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
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
			if (HasPackageReference(doc, packageName))
			{
				onOutput?.Invoke($"⏭️ [{result.RuleId}] {relativePath} already references {packageName} — skipping.");
				return;
			}

			AppendPackageReference(doc, packageName, packageVersion, privateAssets);
			doc.Save(fullPath);
			applied.Add(relativePath);
			onOutput?.Invoke($"✅ [{result.RuleId}] Added <PackageReference Include=\"{packageName}\" /> to {relativePath}");
		}
		catch (Exception ex)
		{
			onOutput?.Invoke($"❌ [{result.RuleId}] Failed to modify {relativePath}: {ex.Message}");
		}
	}

	private static bool HasPackageReference(XDocument doc, string packageName)
		=> doc.Descendants("PackageReference")
			.Any(element => string.Equals(
				element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value,
				packageName,
				StringComparison.OrdinalIgnoreCase));

	private static void AppendPackageReference(XDocument doc, string packageName, string? packageVersion, string? privateAssets)
	{
		var itemGroup = ItemGroupHolding(doc, "PackageReference");

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
	}
}
