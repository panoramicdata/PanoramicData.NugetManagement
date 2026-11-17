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
			if (!TryReadJsonObject(fullPath, relativePath, result, onOutput, out var root))
			{
				return;
			}

			var target = FindObject(root, segments[..^1]);
			if (target is null || !target.Remove(segments[^1]))
			{
				return;
			}

			File.WriteAllText(fullPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
			applied.Add(relativePath);
			Say(onOutput, $"✅ [{result.RuleId}] Removed {propertyPath} from {relativePath}");
		}
		catch (JsonException ex)
		{
			Say(onOutput, $"❌ [{result.RuleId}] Failed to parse {relativePath}: {ex.Message}");
		}
	}

	private static bool TryReadJsonObject(string fullPath, string relativePath, RuleResult result, Action<string>? onOutput, out JsonObject root)
	{
		var json = File.ReadAllText(fullPath);
		var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
		if (node is JsonObject parsed)
		{
			root = parsed;
			return true;
		}

		root = [];
		Say(onOutput, $"⏭️ [{result.RuleId}] {relativePath} root is not a JSON object.");
		return false;
	}

	private static JsonObject? FindObject(JsonObject root, string[] segments)
	{
		var target = root;
		foreach (var segment in segments)
		{
			if (!target.TryGetPropertyValue(segment, out var child) || child is not JsonObject childObject)
			{
				return null;
			}

			target = childObject;
		}

		return target;
	}

	private static JsonObject GetOrCreateObject(JsonObject root, string[] segments)
	{
		var target = root;
		foreach (var segment in segments)
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

		return target;
	}

	private static bool AlreadySets(JsonObject target, string leaf, string propertyValue)
		=> target.TryGetPropertyValue(leaf, out var existing) && existing is JsonValue existingValue
			&& string.Equals(existingValue.ToJsonString().Trim('"'), propertyValue, StringComparison.Ordinal);

	private static bool TryCreateJsonFile(
		string fullPath,
		string relativePath,
		string propertyPath,
		string? createContent,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
	{
		if (createContent is null)
		{
			Say(onOutput, $"⏭️ [{result.RuleId}] {relativePath} does not exist — cannot set {propertyPath}.");
			return false;
		}

		EnsureDirectory(fullPath);
		File.WriteAllText(fullPath, createContent);
		applied.Add(relativePath);
		Say(onOutput, $"✅ [{result.RuleId}] Created {relativePath}");
		return true;
	}

	/// <summary>Sets a string property in a JSON file, leaving a missing file alone.</summary>
	public static void EnsureJsonProperty(
		string localPath,
		string relativePath,
		string propertyPath,
		string propertyValue,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput)
		=> EnsureJsonProperty(localPath, relativePath, propertyPath, propertyValue, result, applied, onOutput, new JsonPropertyOptions());

	/// <summary>How <see cref="EnsureJsonProperty(string, string, string, string, RuleResult, List{string}, Action{string}?, JsonPropertyOptions)"/> creates a missing file and writes the value.</summary>
	internal sealed class JsonPropertyOptions
	{
		/// <summary>
		/// When supplied, the content to write if the file does not exist. Without it a missing file is
		/// left alone: a rule that only knows the property cannot invent the rest of the document.
		/// </summary>
		public string? CreateContent { get; init; }

		/// <summary>
		/// How to write the value: <c>string</c> (the default), <c>bool</c> or <c>number</c>. Writing
		/// <c>"true"</c> where a schema wants <c>true</c> is not a fix.
		/// </summary>
		public string ValueKind { get; init; } = "string";
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
	/// <param name="options">When to create a missing file, and how to write the value.</param>
	public static void EnsureJsonProperty(
		string localPath,
		string relativePath,
		string propertyPath,
		string propertyValue,
		RuleResult result,
		List<string> applied,
		Action<string>? onOutput,
		JsonPropertyOptions options)
	{
		var createContent = options.CreateContent;
		var valueKind = options.ValueKind;
		var fullPath = ResolvePath(localPath, relativePath);
		if (!File.Exists(fullPath) && !TryCreateJsonFile(fullPath, relativePath, propertyPath, createContent, result, applied, onOutput))
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
			if (!TryReadJsonObject(fullPath, relativePath, result, onOutput, out var root))
			{
				return;
			}

			var target = GetOrCreateObject(root, segments[..^1]);
			var leaf = segments[^1];
			if (AlreadySets(target, leaf, propertyValue))
			{
				Say(onOutput, $"⏭️ [{result.RuleId}] {relativePath} already sets {propertyPath} to {propertyValue} — skipping.");
				return;
			}

			target[leaf] = CreateJsonValue(propertyValue, valueKind);

			File.WriteAllText(fullPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
			if (!applied.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
			{
				applied.Add(relativePath);
			}

			Say(onOutput, $"✅ [{result.RuleId}] Set {propertyPath} to {propertyValue} in {relativePath}");
		}
		catch (JsonException ex)
		{
			Say(onOutput, $"❌ [{result.RuleId}] Failed to parse {relativePath}: {ex.Message}");
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
			Say(onOutput, $"❌ [{result.RuleId}] {patterns.Length} pattern(s) but {replacements.Length} replacement(s) — skipping.");
			return;
		}

		var fullPath = ResolvePath(localPath, relativePath);
		if (!File.Exists(fullPath))
		{
			Say(onOutput, $"⏭️ [{result.RuleId}] {relativePath} does not exist — cannot edit.");
			return;
		}

		var original = File.ReadAllText(fullPath);
		if (!TryApplyPatterns(original, patterns, replacements, relativePath, result, onOutput, out var content))
		{
			return;
		}

		if (content == original)
		{
			Say(onOutput, $"⏭️ [{result.RuleId}] Nothing to change in {relativePath} — skipping.");
			return;
		}

		File.WriteAllText(fullPath, content);
		applied.Add(relativePath);
		Say(onOutput, $"✅ [{result.RuleId}] Updated {relativePath}");
	}

	private static bool TryApplyPatterns(
		string original,
		string[] patterns,
		string[] replacements,
		string relativePath,
		RuleResult result,
		Action<string>? onOutput,
		out string content)
	{
		content = original;

		for (var i = 0; i < patterns.Length; i++)
		{
			try
			{
				content = Regex.Replace(content, patterns[i], replacements[i], RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
			}
			catch (RegexParseException ex)
			{
				Say(onOutput, $"❌ [{result.RuleId}] Invalid pattern '{patterns[i]}': {ex.Message}");
				return false;
			}
			catch (RegexMatchTimeoutException)
			{
				Say(onOutput, $"❌ [{result.RuleId}] Pattern '{patterns[i]}' timed out against {relativePath}.");
				return false;
			}
		}

		return true;
	}
}
