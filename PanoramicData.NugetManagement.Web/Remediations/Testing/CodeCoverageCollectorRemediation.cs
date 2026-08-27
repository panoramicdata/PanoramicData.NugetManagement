using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations.Testing;

/// <summary>
/// Sets up Microsoft.Testing.Extensions.CodeCoverage and removes the coverlet packages it replaces.
/// </summary>
public sealed class CodeCoverageCollectorRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "TST-04";

	/// <inheritdoc />
	protected override void ApplyCore(
		string localPath,
		RuleResult result,
		Dictionary<string, object> data,
		string remediationType,
		List<string> applied,
		Action<string>? onOutput)
	{
		if (remediationType != "ensure_code_coverage_setup")
		{
			base.ApplyCore(localPath, result, data, remediationType, applied, onOutput);
			return;
		}

		var usesCpm = data.TryGetValue("uses_cpm", out var usesCpmObj) && usesCpmObj is bool usesCpmValue && usesCpmValue;
		var pinnedInProps = data.TryGetValue("pinned_in_props", out var pinnedObj) && pinnedObj is bool pinnedValue && pinnedValue;
		var referencedInTestProject = data.TryGetValue("referenced_in_test_project", out var referencedObj) && referencedObj is bool referencedValue && referencedValue;
		var targetProject = data.TryGetValue("target_project", out var projectObj) && projectObj is string project ? project : null;
		var packageName = data.TryGetValue("package_name", out var packageObj) && packageObj is string package ? package : Standards.CodeCoveragePackage;
		var packageVersion = data.TryGetValue("package_version", out var versionObj) && versionObj is string version ? version : Standards.CodeCoverageVersion;
		var deadPackages = ReadStrings(data, "dead_packages");
		var projects = ReadStrings(data, "projects");

		if (usesCpm && !pinnedInProps)
		{
			RemediationHelpers.AddPackageVersion(localPath, packageName, packageVersion, result, applied, onOutput);
		}

		if (!referencedInTestProject)
		{
			if (string.IsNullOrWhiteSpace(targetProject))
			{
				onOutput?.Invoke($"⏭️ [{result.RuleId}] No test project was identified — cannot add {packageName} reference automatically.");
			}
			else
			{
				RemediationHelpers.EnsurePackageReference(
					localPath,
					targetProject,
					packageName,
					usesCpm ? null : packageVersion,
					"all",
					result,
					applied,
					onOutput);
			}
		}

		// The coverlet packages are inert under Microsoft.Testing.Platform, so leaving them behind
		// leaves coverage configuration that looks alive and collects nothing.
		foreach (var deadPackage in deadPackages)
		{
			if (projects.Length > 0)
			{
				RemediationHelpers.RemovePackageReference(localPath, deadPackage, projects, result, applied, onOutput);
			}

			RemediationHelpers.RemovePackageVersion(localPath, deadPackage, result, applied, onOutput);
		}
	}

	private static string[] ReadStrings(Dictionary<string, object> data, string key)
		=> data.TryGetValue(key, out var value)
			? value switch
			{
				string[] strings => strings,
				object[] objects => [.. objects.OfType<string>()],
				_ => []
			}
			: [];
}
