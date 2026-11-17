using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations;

/// <summary>The Packages handlers a <see cref="DataDrivenRemediation"/> dispatches to by remediation type.</summary>
internal static class DataDrivenPackagesHandlers
{
	internal static void EnsureXmlProperty(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		if (data["property_name"] is string xpn && data["property_value"] is string xpv)
		{
			var file = data.TryGetValue("file", out var fObj) && fObj is string f ? f : "Directory.Build.props";
			RemediationHelpers.EnsureXmlProperty(localPath, file, xpn, xpv, result, applied, onOutput);
		}
	}

	internal static void EnsureCsprojProperty(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		if (data["property_name"] is string cpn && data["property_value"] is string cpv)
		{
			foreach (var proj in DataDrivenRemediation.ReadStrings(data, "file", "projects"))
			{
				if (cpn == "TargetFramework")
				{
					RemediationHelpers.EnsureTargetFramework(localPath, proj, cpv, result, applied, onOutput);
				}
				else
				{
					RemediationHelpers.EnsureXmlProperty(localPath, proj, cpn, cpv, result, applied, onOutput);
				}
			}
		}
	}

	internal static void AddSlnxFileEntries(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		var missingFiles = DataDrivenRemediation.ReadStrings(data, "missing_files");
		if (data.TryGetValue("file", out var slnxFile) && slnxFile is string sf && missingFiles.Length > 0)
		{
			RemediationHelpers.AddSlnxFileEntries(localPath, sf, missingFiles, result, applied, onOutput);
		}
	}

	internal static void AddPackageVersion(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		if (data["package_name"] is string pkgName &&
			data.TryGetValue("package_version", out var pvObj) && pvObj is string pkgVersion)
		{
			RemediationHelpers.AddPackageVersion(localPath, pkgName, pkgVersion, result, applied, onOutput);
		}
	}

	internal static void UpdatePackageVersions(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		var updates = DataDrivenRemediation.ReadStrings(data, "updates");
		if (updates.Length > 0)
		{
			RemediationHelpers.UpdatePackageVersions(localPath, updates, result, applied, onOutput);
		}
	}

	internal static void RemovePackageReferenceVersions(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		var violatingProjects = DataDrivenRemediation.ReadStrings(data, "projects");
		if (violatingProjects.Length > 0)
		{
			RemediationHelpers.RemovePackageReferenceVersions(localPath, violatingProjects, result, applied, onOutput);
		}
	}

	internal static void RemovePackageReference(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		var rpProjects = DataDrivenRemediation.ReadStrings(data, "projects");
		if (data["package_name"] is string rpPkg && rpProjects.Length > 0)
		{
			RemediationHelpers.RemovePackageReference(localPath, rpPkg, rpProjects, result, applied, onOutput);
		}
	}

	internal static void RemovePackageReferences(string localPath, RuleResult result, Dictionary<string, object> data, List<string> applied, Action<string>? onOutput)
	{
		// Each entry is "project|package": the pairs the rule proved are pruned, never a whole
		// package across every project that mentions it.
		foreach (var removal in DataDrivenRemediation.ReadStrings(data, "removals"))
		{
			var parts = removal.Split('|', 2);
			if (parts.Length == 2)
			{
				RemediationHelpers.RemovePackageReference(localPath, parts[1], [parts[0]], result, applied, onOutput);
			}
		}
	}
}
