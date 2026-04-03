using PanoramicData.NugetManagement.Models;
using System.Text.Json;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that version.json exists for Nerdbank.GitVersioning.
/// </summary>
public class NerdbankVersionJsonRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "VER-01";

	/// <inheritdoc />
	public override string RuleName => "version.json exists";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Versioning;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent("version.json");
		if (content is null)
		{
			return Task.FromResult(Fail(
				"version.json not found at repository root.",
				new RuleAdvisory
				{
					Summary = "Create version.json with a version and publicReleaseRefSpec for Nerdbank.GitVersioning.",
					Detail = "No `version.json` file was found at the repository root. Create one with a `version` property and `publicReleaseRefSpec` array for Nerdbank.GitVersioning.",
						Data = new()
						{
							["expected_path"] = "version.json",
							["template_content"] = Standards.VersionJsonContent
						}
					}));
		}

		try
		{
			using var doc = JsonDocument.Parse(content);
			var root = doc.RootElement;

			if (!root.TryGetProperty("version", out var versionElement) || string.IsNullOrWhiteSpace(versionElement.GetString()))
			{
				return Task.FromResult(Fail(
					"version.json is missing a non-empty 'version' value.",
					new RuleAdvisory
					{
						Summary = "Set the 'version' property in version.json (for example, \"1.0\").",
						Detail = "The `version.json` file exists but is missing a non-empty `version` value. Set it to a valid version string (e.g. `\"1.0\"`).",
						Data = new() { ["file"] = "version.json" }
					}));
			}

			if (!root.TryGetProperty("publicReleaseRefSpec", out var refSpecElement) || refSpecElement.ValueKind != JsonValueKind.Array)
			{
				return Task.FromResult(Fail(
					"version.json is missing 'publicReleaseRefSpec' array.",
					new RuleAdvisory
					{
						Summary = "Add publicReleaseRefSpec with patterns for release branches/tags.",
						Detail = "The `version.json` file is missing a `publicReleaseRefSpec` array. Add it with patterns for release branches/tags.",
						Data = new() { ["file"] = "version.json" }
					}));
			}

			var actualRefSpecs = refSpecElement
				.EnumerateArray()
				.Where(item => item.ValueKind == JsonValueKind.String)
				.Select(item => item.GetString())
				.Where(value => !string.IsNullOrWhiteSpace(value))
				.Select(value => value!)
				.ToList();

			var expectedRefSpecs = context.Options.Publishing.PublicReleaseRefSpec;
			var missing = expectedRefSpecs
				.Where(expected => !actualRefSpecs.Contains(expected, StringComparer.Ordinal))
				.ToList();

			return Task.FromResult(missing.Count == 0
				? Pass("version.json found with version and expected publicReleaseRefSpec patterns.")
				: Fail(
					"version.json does not include all expected publicReleaseRefSpec patterns.",
					new RuleAdvisory
					{
						Summary = $"Add missing patterns: {string.Join(", ", missing)}",
						Detail = $"The `version.json` file is missing expected `publicReleaseRefSpec` patterns: {string.Join(", ", missing)}.",
						Data = new() { ["file"] = "version.json", ["missing_patterns"] = missing.ToArray() }
					}));
		}
		catch (JsonException)
		{
			return Task.FromResult(Fail(
				"version.json is not valid JSON.",
				new RuleAdvisory
				{
					Summary = "Fix JSON syntax in version.json.",
					Detail = "The `version.json` file is not valid JSON. Fix the syntax errors.",
					Data = new() { ["file"] = "version.json" }
				}));
		}
	}
}

/// <summary>
/// Checks that Nerdbank.GitVersioning is referenced.
/// </summary>
public class NerdbankPackageReferencedRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "VER-02";

	/// <inheritdoc />
	public override string RuleName => "Nerdbank.GitVersioning referenced";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Versioning;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var dirPackages = context.GetFileContent("Directory.Packages.props");
		var allCsprojs = context.FindFiles(".csproj")
			.Select(context.GetFileContent)
			.Where(c => c is not null);

		var inCpm = Contains(dirPackages, "Nerdbank.GitVersioning");
		var inCsproj = allCsprojs.Any(c => Contains(c, "Nerdbank.GitVersioning"));

		return Task.FromResult(inCpm || inCsproj
			? Pass("Nerdbank.GitVersioning is referenced.")
			: Fail(
				"Nerdbank.GitVersioning is not referenced in Directory.Packages.props or any .csproj.",
				new RuleAdvisory
				{
					Summary = "Add a PackageVersion for Nerdbank.GitVersioning to Directory.Packages.props.",
					Detail = "Nerdbank.GitVersioning is not referenced in `Directory.Packages.props` or any `.csproj`. Add a `<PackageVersion>` entry for `Nerdbank.GitVersioning`.",
					Data = new() { ["expected_package"] = "Nerdbank.GitVersioning" }
				}));
	}
}

/// <summary>
/// Checks that global.json exists with the correct SDK version.
/// </summary>
public class GlobalJsonRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "VER-03";

	/// <inheritdoc />
	public override string RuleName => "global.json exists with SDK pin";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Versioning;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent("global.json");
		if (content is null)
		{
			return Task.FromResult(Fail(
				"global.json not found at repository root.",
				new RuleAdvisory
				{
					Summary = $"Create global.json pinning SDK version to {Standards.LatestDotNetSdkVersion} with rollForward: latestFeature.",
					Detail = $"No `global.json` file was found at the repository root. Create one pinning the SDK version to `{Standards.LatestDotNetSdkVersion}` with `rollForward: latestFeature`.",
						Data = new()
						{
							["expected_path"] = "global.json",
							["latest_sdk"] = Standards.LatestDotNetSdkVersion,
							["template_content"] = Standards.GlobalJsonContent
						}
					}));
		}

		return Task.FromResult(Contains(content, Standards.LatestDotNetSdkVersion)
			? Pass($"global.json found with SDK version {Standards.LatestDotNetSdkVersion}.")
			: Fail(
				$"global.json does not reference SDK version {Standards.LatestDotNetSdkVersion}.",
				new RuleAdvisory
				{
					Summary = $"Update the sdk.version in global.json to {Standards.LatestDotNetSdkVersion}.",
					Detail = $"The `global.json` file does not reference SDK version `{Standards.LatestDotNetSdkVersion}`. Update the `sdk.version` property.",
					Data = new() { ["file"] = "global.json", ["latest_sdk"] = Standards.LatestDotNetSdkVersion }
				}));
	}
}
