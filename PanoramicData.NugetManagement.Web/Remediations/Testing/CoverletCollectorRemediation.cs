using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations.Testing;

/// <summary>Adds coverlet.collector package reference.</summary>
public sealed class CoverletCollectorRemediation : DataDrivenRemediation
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
		if (remediationType != "ensure_coverlet_collector_setup")
		{
			base.ApplyCore(localPath, result, data, remediationType, applied, onOutput);
			return;
		}

		var usesCpm = data.TryGetValue("uses_cpm", out var usesCpmObj) && usesCpmObj is bool usesCpmValue && usesCpmValue;
		var pinnedInProps = data.TryGetValue("pinned_in_props", out var pinnedObj) && pinnedObj is bool pinnedValue && pinnedValue;
		var referencedInTestProject = data.TryGetValue("referenced_in_test_project", out var referencedObj) && referencedObj is bool referencedValue && referencedValue;
		var targetProject = data.TryGetValue("target_project", out var projectObj) && projectObj is string project ? project : null;
		var packageName = data.TryGetValue("package_name", out var packageObj) && packageObj is string package ? package : "coverlet.collector";
		var packageVersion = data.TryGetValue("package_version", out var versionObj) && versionObj is string version ? version : "8.0.1";

		if (usesCpm && !pinnedInProps)
		{
			RemediationHelpers.AddPackageVersion(localPath, packageName, packageVersion, result, applied, onOutput);
		}

		if (!referencedInTestProject)
		{
			if (string.IsNullOrWhiteSpace(targetProject))
			{
				onOutput?.Invoke($"⏭️ [{result.RuleId}] No test project was identified — cannot add {packageName} reference automatically.");
				return;
			}

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
}
