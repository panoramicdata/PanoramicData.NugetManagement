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
	/// Resolves a relative path to a full path under the local repo root.
	/// </summary>
	public static string ResolvePath(string localPath, string relativePath)
		=> Path.Combine(localPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

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

			var added = AppendMissingItems(GetOrCreateArray(root, arrayProperty), items);

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
			RemovePackageReferenceFromProject(localPath, packageName, projectPath, result, applied, onOutput);
		}
	}

	private static JsonArray GetOrCreateArray(JsonObject root, string arrayProperty)
	{
		if (root.TryGetPropertyValue(arrayProperty, out var existing) && existing is JsonArray existingArray)
		{
			return existingArray;
		}

		JsonArray array = [];
		root[arrayProperty] = array;
		return array;
	}

	private static int AppendMissingItems(JsonArray array, string[] items)
	{
		var currentValues = array
			.Select(n => n?.GetValue<string>())
			.Where(v => v is not null)
			.ToHashSet(StringComparer.Ordinal);

		var added = 0;
		foreach (var item in items.Where(item => !currentValues.Contains(item)))
		{
			array.Add(JsonValue.Create(item));
			added++;
		}

		return added;
	}

	private static void RemovePackageReferenceFromProject(
		string localPath,
		string packageName,
		string projectPath,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		var fullPath = ResolvePath(localPath, projectPath);
		if (!File.Exists(fullPath))
		{
			onOutput?.Invoke($"⏭️ [{result.RuleId}] {projectPath} does not exist — skipping.");
			return;
		}

		try
		{
			var doc = XDocument.Load(fullPath, LoadOptions.PreserveWhitespace);
			var toRemove = ReferencesTo(doc, packageName);

			if (toRemove.Count == 0)
			{
				onOutput?.Invoke($"⏭️ [{result.RuleId}] {projectPath} does not reference {packageName} — skipping.");
				return;
			}

			RemoveAndPrune(doc, toRemove);

			doc.Save(fullPath);
			applied.Add(projectPath);
			onOutput?.Invoke($"✅ [{result.RuleId}] Removed {packageName} PackageReference from {projectPath}");
		}
		catch (Exception ex)
		{
			onOutput?.Invoke($"❌ [{result.RuleId}] Failed to modify {projectPath}: {ex.Message}");
		}
	}

	private static List<XElement> ReferencesTo(XDocument doc, string packageName)
		=> FindByInclude(doc, "PackageReference", packageName);

	private static List<XElement> FindByInclude(XDocument doc, string elementName, string include)
		=> [.. doc.Descendants(elementName)
			.Where(e => string.Equals(e.Attribute("Include")?.Value, include, StringComparison.OrdinalIgnoreCase))];

	private static void RemoveAndPrune(XDocument doc, List<XElement> toRemove)
	{
		// Remove the preceding whitespace/newline node too for clean diffs
		toRemove.ForEach(RemoveWithLeadingWhitespace);

		// Remove any now-empty ItemGroups
		doc.Descendants("ItemGroup")
			.Where(g => !g.HasElements)
			.ToList()
			.ForEach(RemoveWithLeadingWhitespace);
	}

	private static void RemoveWithLeadingWhitespace(XElement element)
	{
		if (element.PreviousNode is XText { Value: var ws } && ws.Trim().Length == 0)
		{
			element.PreviousNode.Remove();
		}

		element.Remove();
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
			var toRemove = FindByInclude(doc, "PackageVersion", packageName);

			if (toRemove.Count == 0)
			{
				return;
			}

			// Remove the preceding whitespace/newline node too, for clean diffs
			toRemove.ForEach(RemoveWithLeadingWhitespace);

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
			if (RewriteFile(file, root, patterns, replacements, applied, result, onOutput))
			{
				changedCount++;
			}
		}

		onOutput?.Invoke(changedCount == 0
			? $"⏭️ [{result.RuleId}] Nothing matched the patterns — no files changed."
			: $"✅ [{result.RuleId}] Rewrote {changedCount} file(s).");
	}

	private static bool RewriteFile(
		string file,
		string root,
		string[] patterns,
		string[] replacements,
		List<string> applied,
		RuleResult result,
		Action<string>? onOutput)
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
				return false;
			}

			File.WriteAllText(file, updated);
			applied.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexParseException)
		{
			onOutput?.Invoke($"❌ [{result.RuleId}] Failed to rewrite {file}: {ex.Message}");
			return false;
		}
	}
}
