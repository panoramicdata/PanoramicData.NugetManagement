using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that no project file sets a Copyright holding only our own notice, which would override the
/// shared Directory.Build.props value. A Copyright naming another holder is third-party attribution
/// and is intentionally left alone. Project files in test-fixture folders are ignored.
/// </summary>
public class CopyrightOverrideRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "LIC-05";

	/// <inheritdoc />
	public override string RuleName => "No Copyright override in project files";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Licensing;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var projects = context.FilePaths
			.Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && !GovernedPaths.IsTestFixture(path))
			.ToList();

		var holder = context.Options.ExpectedCopyrightHolder;
		var overriding = projects
			.Where(project => CopyrightOverrideClassifier.IsOwnOverride(context.GetFileContent(project), holder))
			.ToArray();
		var leftAlone = projects
			.Where(project => !overriding.Contains(project) && MsBuildProperties.Has(context.GetFileContent(project), "Copyright"))
			.ToArray();
		var note = leftAlone.Length == 0
			? string.Empty
			: $" {leftAlone.Length} project file(s) set a Copyright naming another holder or other text and are intentionally left alone: {string.Join(", ", leftAlone)}.";

		if (overriding.Length == 0)
		{
			return Task.FromResult(projects.Count == 0
				? NotApplicable("The repository has no project files.")
				: Pass($"None of {projects.Count} project file(s) overrides Copyright with only our own notice.{note}"));
		}

		return Task.FromResult(Fail(
			$"{string.Join(", ", overriding)}: sets Copyright, overriding Directory.Build.props.{note}",
			new RuleAdvisory
			{
				Summary = "Remove <Copyright> from the project files so Directory.Build.props applies.",
				Detail = $"These project files set `<Copyright>` to only our own notice, which overrides the shared value in `Directory.Build.props` and can leave a stale or wrong notice in the package: {string.Join(", ", overriding)}. Delete the element. A csproj Copyright naming another holder (third-party attribution) is intentionally left alone.{note}",
				Data = new()
				{
					["projects"] = overriding,
					["remediation_type"] = "remove_xml_property",
					["property_name"] = "Copyright",
					["holder"] = holder
				}
			}));
	}
}
