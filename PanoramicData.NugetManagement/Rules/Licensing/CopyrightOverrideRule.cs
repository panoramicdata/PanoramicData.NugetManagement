using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that no project file sets Copyright, which would override the shared Directory.Build.props value.
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
			.Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
			.ToList();

		var overriding = projects
			.Where(project => HasMsBuildProperty(context.GetFileContent(project), "Copyright"))
			.ToArray();

		if (overriding.Length == 0)
		{
			return Task.FromResult(projects.Count == 0
				? NotApplicable("The repository has no project files.")
				: Pass($"None of {projects.Count} project file(s) sets Copyright."));
		}

		return Task.FromResult(Fail(
			$"{string.Join(", ", overriding)}: sets Copyright, overriding Directory.Build.props.",
			new RuleAdvisory
			{
				Summary = "Remove <Copyright> from the project files so Directory.Build.props applies.",
				Detail = $"These project files set `<Copyright>`, which overrides the shared value in `Directory.Build.props` and can leave a stale or wrong notice in the package: {string.Join(", ", overriding)}. Delete the element.",
				Data = new()
				{
					["projects"] = overriding,
					["remediation_type"] = "remove_xml_property",
					["property_name"] = "Copyright"
				}
			}));
	}
}
