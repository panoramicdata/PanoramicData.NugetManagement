using System.Text.Json;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations;

/// <summary>
/// Base class for data-driven remediations that read advisory Data to determine
/// what to do. Subclasses only need to implement <see cref="RuleId"/> and
/// optionally override <see cref="ApplyCore"/> for custom logic.
/// </summary>
public abstract class DataDrivenRemediation : IRemediation
{
	/// <summary>
	/// What <see cref="ApplyCore"/> reads, per remediation type: one entry per requirement, each
	/// listing the keys that satisfy it, any one of which will do.
	/// </summary>
	/// <remarks>
	/// A type absent from this table states no requirement — <c>ensure_code_coverage_setup</c> has no
	/// case here at all, and belongs entirely to the subclass that overrides <see cref="ApplyCore"/>
	/// for it.
	/// </remarks>
	private static readonly Dictionary<string, string[][]> _requiredData = new(StringComparer.Ordinal)
	{
		["ensure_xml_property"] = [["property_name"], ["property_value"]],
		["ensure_csproj_property"] = [["property_name"], ["property_value"], ["file", "projects"]],
		["ensure_json_property"] = [["property_path"], ["property_value"], ["file", "files"]],
		["ensure_json_properties"] = [["file"], ["properties"]],
		["ensure_checkout_fetch_depth"] = [["file"]],
		["replace_regex_in_file"] = [["file"], ["patterns"], ["replacements"]],
		["replace_regex_in_files"] = [["globs"], ["patterns"], ["replacements"]],
		["remove_json_property"] = [["file"], ["property_path"]],
		["append_line"] = [["file"], ["line_content"]],
		["prepend_line"] = [["file"], ["line_content"]],
		["append_lines"] = [["file"], ["lines"]],
		["add_slnx_file_entries"] = [["file"], ["missing_files"]],
		["replace_file_content"] = [["file"], ["new_content"]],
		["replace_in_file"] = [["file"], ["old_text"], ["new_text"]],
		["add_package_version"] = [["package_name"], ["package_version"]],
		["update_package_versions"] = [["updates"]],
		["remove_packagereference_versions"] = [["projects"]],
		["remove_packagereference"] = [["package_name"], ["projects"]],
		["remove_packagereferences"] = [["removals"]],
		["add_json_array_items"] = [["file"], ["array_property"], ["items"]],
		["delete_file"] = [["file"]]
	};

	/// <inheritdoc />
	public abstract string RuleId { get; }

	/// <inheritdoc />
	public virtual bool CanRemediate(RuleResult result)
	{
		if (result.Passed || result.Advisory is null)
		{
			return false;
		}

		var data = result.Advisory.Data;

		// create_file: expected_path + template_content
		if (data.ContainsKey("expected_path") && data.ContainsKey("template_content"))
		{
			return true;
		}

		// All other types identified by remediation_type key. Naming a type is not enough: the data it
		// reads has to be there too, or the dashboard draws a wrench, counts the fix, applies nothing,
		// and reports success — which is exactly what PKG-05/06/07 did once their rules renamed the
		// payload out from under this class.
		if (data.TryGetValue("remediation_type", out var rtObj) && rtObj is string rt)
		{
			return HasRequiredData(data, rt) && rt is "ensure_xml_property"
						or "ensure_csproj_property"
						or "ensure_code_coverage_setup"
						or "ensure_json_property"
						or "ensure_json_properties"
						or "ensure_checkout_fetch_depth"
						or "replace_regex_in_file"
						or "remove_json_property"
						or "replace_regex_in_files"
						or "append_line"
						or "prepend_line"
						or "add_slnx_file_entries"
						or "replace_file_content"
						or "replace_in_file"
						or "append_lines"
						or "add_package_version"
						or "update_package_versions"
						or "remove_packagereference_versions"
						or "remove_packagereference"
						or "remove_packagereferences"
						or "add_json_array_items"
						or "delete_file";
		}

		return false;
	}

	/// <inheritdoc />
	public virtual void Apply(string localPath, RuleResult result, List<string> applied, Action<string>? onOutput)
	{
		var data = result.Advisory!.Data;

		// Determine remediation type
		var remediationType = data.TryGetValue("remediation_type", out var rtObj) && rtObj is string rt ? rt : null;

		// Fallback: create_file from expected_path + template_content
		if (remediationType is null && data.ContainsKey("expected_path") && data.ContainsKey("template_content"))
		{
			remediationType = "create_file";
		}

		if (remediationType is null)
		{
			return;
		}

		ApplyCore(localPath, result, data, remediationType, applied, onOutput);
	}

	/// <summary>
	/// Applies the remediation based on the type and data.
	/// Override in subclasses for custom logic.
	/// </summary>
	protected virtual void ApplyCore(
		string localPath,
		RuleResult result,
		Dictionary<string, object> data,
		string remediationType,
		List<string> applied,
		Action<string>? onOutput)
	{
		if (_handlers.TryGetValue(remediationType, out var handler))
		{
			handler(localPath, result, data, applied, onOutput);
		}
	}

	private delegate void Handler(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput);

	private static readonly Dictionary<string, Handler> _handlers = new()
	{
		["create_file"] = DataDrivenFilesHandlers.CreateFile,
		["ensure_xml_property"] = DataDrivenPackagesHandlers.EnsureXmlProperty,
		["ensure_csproj_property"] = DataDrivenPackagesHandlers.EnsureCsprojProperty,
		["replace_regex_in_files"] = DataDrivenFilesHandlers.ReplaceRegexInFiles,
		["remove_json_property"] = DataDrivenJsonHandlers.RemoveJsonProperty,
		["ensure_json_property"] = DataDrivenJsonHandlers.EnsureJsonProperty,
		["ensure_checkout_fetch_depth"] = DataDrivenFilesHandlers.EnsureCheckoutFetchDepth,
		["replace_regex_in_file"] = DataDrivenFilesHandlers.ReplaceRegexInFile,
		["ensure_json_properties"] = DataDrivenJsonHandlers.EnsureJsonProperties,
		["append_line"] = DataDrivenFilesHandlers.AppendLine,
		["prepend_line"] = DataDrivenFilesHandlers.PrependLine,
		["add_slnx_file_entries"] = DataDrivenPackagesHandlers.AddSlnxFileEntries,
		["replace_file_content"] = DataDrivenFilesHandlers.ReplaceFileContent,
		["replace_in_file"] = DataDrivenFilesHandlers.ReplaceInFile,
		["append_lines"] = DataDrivenFilesHandlers.AppendLines,
		["add_package_version"] = DataDrivenPackagesHandlers.AddPackageVersion,
		["update_package_versions"] = DataDrivenPackagesHandlers.UpdatePackageVersions,
		["remove_packagereference_versions"] = DataDrivenPackagesHandlers.RemovePackageReferenceVersions,
		["add_json_array_items"] = DataDrivenJsonHandlers.AddJsonArrayItems,
		["remove_packagereference"] = DataDrivenPackagesHandlers.RemovePackageReference,
		["remove_packagereferences"] = DataDrivenPackagesHandlers.RemovePackageReferences,
		["delete_file"] = DataDrivenFilesHandlers.DeleteFile,
	};

	/// <summary>
	/// Checks that the advisory carries everything the remediation type will read.
	/// </summary>
	/// <remarks>
	/// A subclass that overrides <see cref="ApplyCore"/> and reads different keys states its own
	/// requirements by overriding this.
	/// </remarks>
	/// <param name="data">The advisory data.</param>
	/// <param name="remediationType">The remediation type the advisory named.</param>
	/// <returns>Whether the data is complete enough to act on.</returns>
	protected virtual bool HasRequiredData(Dictionary<string, object> data, string remediationType)
		=> (!_requiredData.TryGetValue(remediationType, out var requirements)
			|| requirements.All(alternatives => alternatives.Any(data.ContainsKey)))
			&& (remediationType != "ensure_json_properties"
				|| (data.TryGetValue("properties", out var properties) && ReadStringMap(properties).Count > 0));

	/// <summary>
	/// Reads the strings held under the first of <paramref name="keys"/> that is present, accepting a
	/// single string, a string array, or the <see cref="JsonElement"/> or object array advisory data
	/// becomes once it has been round-tripped through JSON.
	/// </summary>
	/// <remarks>
	/// Every array read in <see cref="ApplyCore"/> goes through here rather than testing for
	/// <c>string[]</c> directly. The row cache normalises its arrays back to <c>string[]</c>, but not
	/// every store does, and a shape this does not recognise is silently no work done rather than an
	/// error — so it accepts every shape an advisory has been seen in.
	/// </remarks>
	/// <param name="data">The advisory data.</param>
	/// <param name="keys">The keys to try, in order.</param>
	/// <returns>The strings found, or an empty array.</returns>
	internal static string[] ReadStrings(Dictionary<string, object> data, params string[] keys)
	{
		foreach (var key in keys)
		{
			if (!data.TryGetValue(key, out var value))
			{
				continue;
			}

			string[]? found = value switch
			{
				string single => [single],
				string[] strings => strings,
				JsonElement { ValueKind: JsonValueKind.String } element => [element.GetString()!],
				JsonElement { ValueKind: JsonValueKind.Array } array =>
				[
						.. array
							.EnumerateArray()
							.Where(item => item.ValueKind == JsonValueKind.String)
							.Select(item => item.GetString()!)
				],
				IEnumerable<object> objects => [.. objects.OfType<string>()],
				_ => null
			};

			if (found is not null)
			{
				return found;
			}
		}

		return [];
	}

	/// <summary>
	/// Reads a string-to-string map in any shape an advisory has been seen in: the rule's own
	/// dictionary, the object dictionary the row cache reads it back as, or a <see cref="JsonElement"/>.
	/// Anything else is empty, which the caller must treat as no work, never as success.
	/// </summary>
	/// <param name="value">The advisory value.</param>
	/// <returns>The map, or an empty one.</returns>
	internal static Dictionary<string, string> ReadStringMap(object? value) => value switch
	{
		Dictionary<string, string> map => map,
		Dictionary<string, object> objects => objects
			.Where(pair => pair.Value is string)
			.ToDictionary(pair => pair.Key, pair => (string)pair.Value),
		JsonElement { ValueKind: JsonValueKind.Object } element => element
			.EnumerateObject()
			.Where(property => property.Value.ValueKind == JsonValueKind.String)
			.ToDictionary(property => property.Name, property => property.Value.GetString()!),
		_ => []
	};
}
