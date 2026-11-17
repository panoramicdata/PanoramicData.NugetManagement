using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations;

/// <summary>The Json handlers a <see cref="DataDrivenRemediation"/> dispatches to by remediation type.</summary>
internal static class DataDrivenJsonHandlers
{
	internal static void RemoveJsonProperty(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		if (data.TryGetValue("file", out var rjFile) && rjFile is string rjf &&
			data["property_path"] is string rjPath)
		{
			RemediationHelpers.RemoveJsonProperty(localPath, rjf, rjPath, result, applied, onOutput);
		}
	}

	internal static void EnsureJsonProperty(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		if (data.TryGetValue("property_path", out var jpPathObj) && jpPathObj is string jpPath &&
			data.TryGetValue("property_value", out var jpValueObj) && jpValueObj is string jpValue)
		{
			var createContent = data.TryGetValue("create_content", out var ccObj) && ccObj is string cc ? cc : null;
			var valueKind = data.TryGetValue("value_kind", out var vkObj) && vkObj is string vk ? vk : "string";

			foreach (var jsonPath in DataDrivenRemediation.ReadStrings(data, "file", "files"))
			{
				RemediationHelpers.EnsureJsonProperty(
					localPath, jsonPath, jpPath, jpValue, result, applied, onOutput, createContent, valueKind);
			}
		}
	}

	internal static void EnsureJsonProperties(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		// The plural form: a rule that can fail on more than one property sets only the ones that
		// are actually wrong, leaving the rest of the file — and any already-conformant value —
		// untouched.
		if (data.TryGetValue("file", out var jpsFile) && jpsFile is string jpsf &&
			DataDrivenRemediation.ReadStringMap(data["properties"]) is { Count: > 0 } jpsProperties)
		{
			foreach (var (jpsPath, jpsValue) in jpsProperties)
			{
				RemediationHelpers.EnsureJsonProperty(localPath, jpsf, jpsPath, jpsValue, result, applied, onOutput);
			}
		}
	}

	internal static void AddJsonArrayItems(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		var items = DataDrivenRemediation.ReadStrings(data, "items");
		if (data.TryGetValue("file", out var jFile) && jFile is string jsonFile &&
			data["array_property"] is string arrayProp && items.Length > 0)
		{
			RemediationHelpers.AddJsonArrayItems(localPath, jsonFile, arrayProp, items, result, applied, onOutput);
		}
	}
}
