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
		switch (remediationType)
		{
			case "create_file":
				if (data["expected_path"] is string path && data["template_content"] is string content)
				{
					RemediationHelpers.CreateFile(localPath, path, content, result, applied, onOutput);
				}

				break;

			case "ensure_xml_property":
				if (data["property_name"] is string xpn && data["property_value"] is string xpv)
				{
					var file = data.TryGetValue("file", out var fObj) && fObj is string f ? f : "Directory.Build.props";
					RemediationHelpers.EnsureXmlProperty(localPath, file, xpn, xpv, result, applied, onOutput);
				}

				break;

			case "ensure_csproj_property":
				if (data["property_name"] is string cpn && data["property_value"] is string cpv)
				{
					foreach (var proj in ReadStrings(data, "file", "projects"))
					{
						RemediationHelpers.EnsureXmlProperty(localPath, proj, cpn, cpv, result, applied, onOutput);
					}
				}

				break;

			case "replace_regex_in_files":
				var rrGlobs = ReadStrings(data, "globs");
				var rrPatterns = ReadStrings(data, "patterns");
				var rrReplacements = ReadStrings(data, "replacements");
				if (rrGlobs.Length > 0 && rrPatterns.Length > 0 && rrReplacements.Length > 0)
				{
					RemediationHelpers.ReplaceRegexInFiles(
						localPath, rrGlobs, rrPatterns, rrReplacements, result, applied, onOutput);
				}

				break;

			case "remove_json_property":
				if (data.TryGetValue("file", out var rjFile) && rjFile is string rjf &&
					data["property_path"] is string rjPath)
				{
					RemediationHelpers.RemoveJsonProperty(localPath, rjf, rjPath, result, applied, onOutput);
				}

				break;

			case "ensure_json_property":
				if (data.TryGetValue("property_path", out var jpPathObj) && jpPathObj is string jpPath &&
					data.TryGetValue("property_value", out var jpValueObj) && jpValueObj is string jpValue)
				{
					var createContent = data.TryGetValue("create_content", out var ccObj) && ccObj is string cc ? cc : null;
					var valueKind = data.TryGetValue("value_kind", out var vkObj) && vkObj is string vk ? vk : "string";

					foreach (var jsonPath in ReadStrings(data, "file", "files"))
					{
						RemediationHelpers.EnsureJsonProperty(
							localPath, jsonPath, jpPath, jpValue, result, applied, onOutput, createContent, valueKind);
					}
				}

				break;

			case "ensure_checkout_fetch_depth":
				if (data.TryGetValue("file", out var fdFile) && fdFile is string fdf)
				{
					RemediationHelpers.EnsureCheckoutFetchDepth(localPath, fdf, result, applied, onOutput);
				}

				break;

			case "replace_regex_in_file":
				if (data.TryGetValue("file", out var rrFile) && rrFile is string rrf)
				{
					RemediationHelpers.ReplaceRegexInFile(
						localPath,
						rrf,
						ReadStrings(data, "patterns"),
						ReadStrings(data, "replacements"),
						result,
						applied,
						onOutput);
				}

				break;

			case "ensure_json_properties":
				// The plural form: a rule that can fail on more than one property sets only the ones that
				// are actually wrong, leaving the rest of the file — and any already-conformant value —
				// untouched.
				if (data.TryGetValue("file", out var jpsFile) && jpsFile is string jpsf &&
					data["properties"] is Dictionary<string, string> jpsProperties)
				{
					foreach (var (jpsPath, jpsValue) in jpsProperties)
					{
						RemediationHelpers.EnsureJsonProperty(localPath, jpsf, jpsPath, jpsValue, result, applied, onOutput);
					}
				}

				break;

			case "append_line":
				if (data["line_content"] is string alc && data.TryGetValue("file", out var alFile) && alFile is string alf)
				{
					RemediationHelpers.AppendLine(localPath, alf, alc, result, applied, onOutput);
				}

				break;

			case "prepend_line":
				if (data["line_content"] is string plc && data.TryGetValue("file", out var plFile) && plFile is string plf)
				{
					RemediationHelpers.PrependLine(localPath, plf, plc, result, applied, onOutput);
				}

				break;

			case "add_slnx_file_entries":
				var missingFiles = ReadStrings(data, "missing_files");
				if (data.TryGetValue("file", out var slnxFile) && slnxFile is string sf && missingFiles.Length > 0)
				{
					RemediationHelpers.AddSlnxFileEntries(localPath, sf, missingFiles, result, applied, onOutput);
				}

				break;

			case "replace_file_content":
				if (data.TryGetValue("file", out var rfFile) && rfFile is string rff &&
					data["new_content"] is string newContent)
				{
					RemediationHelpers.ReplaceFileContent(localPath, rff, newContent, result, applied, onOutput);
				}

				break;

			case "replace_in_file":
				if (data.TryGetValue("file", out var riFile) && riFile is string rif &&
					data["old_text"] is string oldText &&
					data["new_text"] is string newText)
				{
					RemediationHelpers.ReplaceInFile(localPath, rif, oldText, newText, result, applied, onOutput);
				}

				break;

			case "append_lines":
				if (data.TryGetValue("file", out var alsFile) && alsFile is string alsf)
				{
					foreach (var line in ReadStrings(data, "lines"))
					{
						RemediationHelpers.AppendLine(localPath, alsf, line, result, applied, onOutput);
					}
				}

				break;

			case "add_package_version":
				if (data["package_name"] is string pkgName &&
					data.TryGetValue("package_version", out var pvObj) && pvObj is string pkgVersion)
				{
					RemediationHelpers.AddPackageVersion(localPath, pkgName, pkgVersion, result, applied, onOutput);
				}

				break;

			case "update_package_versions":
				var updates = ReadStrings(data, "updates");
				if (updates.Length > 0)
				{
					RemediationHelpers.UpdatePackageVersions(localPath, updates, result, applied, onOutput);
				}

				break;

			case "remove_packagereference_versions":
				var violatingProjects = ReadStrings(data, "projects");
				if (violatingProjects.Length > 0)
				{
					RemediationHelpers.RemovePackageReferenceVersions(localPath, violatingProjects, result, applied, onOutput);
				}

				break;

			case "add_json_array_items":
				var items = ReadStrings(data, "items");
				if (data.TryGetValue("file", out var jFile) && jFile is string jsonFile &&
					data["array_property"] is string arrayProp && items.Length > 0)
				{
					RemediationHelpers.AddJsonArrayItems(localPath, jsonFile, arrayProp, items, result, applied, onOutput);
				}

				break;

			case "remove_packagereference":
				var rpProjects = ReadStrings(data, "projects");
				if (data["package_name"] is string rpPkg && rpProjects.Length > 0)
				{
					RemediationHelpers.RemovePackageReference(localPath, rpPkg, rpProjects, result, applied, onOutput);
				}

				break;

			case "delete_file":
				if (data.TryGetValue("file", out var dfFile) && dfFile is string deleteFile)
				{
					RemediationHelpers.DeleteFile(localPath, deleteFile, result, applied, onOutput);
				}

				break;
		}
	}

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
		=> !_requiredData.TryGetValue(remediationType, out var requirements)
			|| requirements.All(alternatives => alternatives.Any(data.ContainsKey));

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
	protected static string[] ReadStrings(Dictionary<string, object> data, params string[] keys)
	{
		foreach (var key in keys)
		{
			if (!data.TryGetValue(key, out var value))
			{
				continue;
			}

			switch (value)
			{
				case string single:
					return [single];
				case string[] strings:
					return strings;
				case JsonElement { ValueKind: JsonValueKind.String } single:
					return [single.GetString()!];
				case JsonElement { ValueKind: JsonValueKind.Array } array:
					return
					[
						.. array
							.EnumerateArray()
							.Where(item => item.ValueKind == JsonValueKind.String)
							.Select(item => item.GetString()!)
					];
				case IEnumerable<object> objects:
					return [.. objects.OfType<string>()];
			}
		}

		return [];
	}
}
