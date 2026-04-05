using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations;

/// <summary>
/// Base class for data-driven remediations that read advisory Data to determine
/// what to do. Subclasses only need to implement <see cref="RuleId"/> and
/// optionally override <see cref="ApplyCore"/> for custom logic.
/// </summary>
public abstract class DataDrivenRemediation : IRemediation
{
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

		// All other types identified by remediation_type key
		if (data.TryGetValue("remediation_type", out var rtObj) && rtObj is string rt)
		{
			return rt is "ensure_xml_property"
						or "ensure_csproj_property"
						or "append_line"
						or "prepend_line"
						or "add_slnx_file_entries"
						or "replace_file_content"
						or "replace_in_file"
						or "append_lines"
						or "add_package_version"
						or "remove_packagereference_versions"
						or "add_json_array_items"
						or "delete_file";
		}

		return false;
	}

	/// <inheritdoc />
	public void Apply(string localPath, RuleResult result, List<string> applied, Action<string>? onOutput)
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
					if (data.TryGetValue("file", out var cfObj) && cfObj is string cf)
					{
						RemediationHelpers.EnsureXmlProperty(localPath, cf, cpn, cpv, result, applied, onOutput);
					}
					else if (data.TryGetValue("projects", out var projObj) && projObj is string[] projects)
					{
						foreach (var proj in projects)
						{
							RemediationHelpers.EnsureXmlProperty(localPath, proj, cpn, cpv, result, applied, onOutput);
						}
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
				if (data.TryGetValue("file", out var slnxFile) && slnxFile is string sf &&
					data["missing_files"] is string[] missingFiles)
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
				if (data.TryGetValue("file", out var alsFile) && alsFile is string alsf &&
					data["lines"] is string[] lines)
				{
					foreach (var line in lines)
					{
						RemediationHelpers.AppendLine(localPath, alsf, line, result, applied, onOutput);
					}
				}
				else if (data.TryGetValue("file", out var alsFile2) && alsFile2 is string alsf2 &&
					data["lines"] is object[] objLines)
				{
					foreach (var line in objLines.OfType<string>())
					{
						RemediationHelpers.AppendLine(localPath, alsf2, line, result, applied, onOutput);
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

			case "remove_packagereference_versions":
				if (data["projects"] is string[] violatingProjects)
				{
					RemediationHelpers.RemovePackageReferenceVersions(localPath, violatingProjects, result, applied, onOutput);
				}
				else if (data["projects"] is object[] objProjects)
				{
					var projects = objProjects.OfType<string>().ToArray();
					RemediationHelpers.RemovePackageReferenceVersions(localPath, projects, result, applied, onOutput);
				}

				break;

			case "add_json_array_items":
				if (data.TryGetValue("file", out var jFile) && jFile is string jsonFile &&
					data["array_property"] is string arrayProp)
				{
					string[] items;
					if (data["items"] is string[] strItems)
					{
						items = strItems;
					}
					else if (data["items"] is object[] objItems)
					{
						items = [.. objItems.OfType<string>()];
					}
					else
					{
						break;
					}

					RemediationHelpers.AddJsonArrayItems(localPath, jsonFile, arrayProp, items, result, applied, onOutput);
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
}
