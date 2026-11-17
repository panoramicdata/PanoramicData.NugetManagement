using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations;

/// <summary>The Files handlers a <see cref="DataDrivenRemediation"/> dispatches to by remediation type.</summary>
internal static class DataDrivenFilesHandlers
{
	internal static void CreateFile(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		if (data["expected_path"] is string path && data["template_content"] is string content)
		{
			RemediationHelpers.CreateFile(localPath, path, content, result, applied, onOutput);
		}
	}

	internal static void ReplaceRegexInFiles(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		var rrGlobs = DataDrivenRemediation.ReadStrings(data, "globs");
		var rrPatterns = DataDrivenRemediation.ReadStrings(data, "patterns");
		var rrReplacements = DataDrivenRemediation.ReadStrings(data, "replacements");
		if (rrGlobs.Length > 0 && rrPatterns.Length > 0 && rrReplacements.Length > 0)
		{
			RemediationHelpers.ReplaceRegexInFiles(
				localPath, rrGlobs, rrPatterns, rrReplacements, result, applied, onOutput);
		}
	}

	internal static void EnsureCheckoutFetchDepth(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		if (data.TryGetValue("file", out var fdFile) && fdFile is string fdf)
		{
			RemediationHelpers.EnsureCheckoutFetchDepth(localPath, fdf, result, applied, onOutput);
		}
	}

	internal static void ReplaceRegexInFile(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		if (data.TryGetValue("file", out var rrFile) && rrFile is string rrf)
		{
			RemediationHelpers.ReplaceRegexInFile(
				localPath,
				rrf,
				DataDrivenRemediation.ReadStrings(data, "patterns"),
				DataDrivenRemediation.ReadStrings(data, "replacements"),
				result,
				applied,
				onOutput);
		}
	}

	internal static void AppendLine(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		if (data["line_content"] is string alc && data.TryGetValue("file", out var alFile) && alFile is string alf)
		{
			RemediationHelpers.AppendLine(localPath, alf, alc, result, applied, onOutput);
		}
	}

	internal static void PrependLine(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		if (data["line_content"] is string plc && data.TryGetValue("file", out var plFile) && plFile is string plf)
		{
			RemediationHelpers.PrependLine(localPath, plf, plc, result, applied, onOutput);
		}
	}

	internal static void ReplaceFileContent(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		if (data.TryGetValue("file", out var rfFile) && rfFile is string rff &&
			data["new_content"] is string newContent)
		{
			RemediationHelpers.ReplaceFileContent(localPath, rff, newContent, result, applied, onOutput);
		}
	}

	internal static void ReplaceInFile(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		if (data.TryGetValue("file", out var riFile) && riFile is string rif &&
			data["old_text"] is string oldText &&
			data["new_text"] is string newText)
		{
			RemediationHelpers.ReplaceInFile(localPath, rif, oldText, newText, result, applied, onOutput);
		}
	}

	internal static void AppendLines(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		if (data.TryGetValue("file", out var alsFile) && alsFile is string alsf)
		{
			foreach (var line in DataDrivenRemediation.ReadStrings(data, "lines"))
			{
				RemediationHelpers.AppendLine(localPath, alsf, line, result, applied, onOutput);
			}
		}
	}

	internal static void DeleteFile(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		if (data.TryGetValue("file", out var dfFile) && dfFile is string deleteFile)
		{
			RemediationHelpers.DeleteFile(localPath, deleteFile, result, applied, onOutput);
		}
	}
}
