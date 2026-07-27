using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that System.Text.Json is preferred over Newtonsoft.Json.
/// </summary>
public class SystemTextJsonPreferredRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "SER-01";

	/// <inheritdoc />
	public override string RuleName => "System.Text.Json preferred over Newtonsoft";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Serialization;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Info;

	private const string _package = "Newtonsoft.Json";

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		// A project referencing the package is the repository's own choice, so it is what this rule
		// is about. A central PackageVersion is only a version pin and is examined separately below:
		// pinning is often the correct way to control a transitive dependency the repository never
		// chose, and flagging that as a violation punishes good practice.
		foreach (var csproj in context.FindFiles(".csproj"))
		{
			if (ReferencesPackageDirectly(context.GetFileContent(csproj), _package))
			{
				return Task.FromResult(Fail(
					$"{_package} is referenced by {csproj}.",
					new RuleAdvisory
					{
						Summary = $"Migrate to System.Text.Json. Remove {_package} references.",
						Detail = $"{_package} is referenced by `{csproj}`. Migrate all serialization code to `System.Text.Json` and remove the {_package} package reference.",
						Data = new() { ["file"] = csproj, ["package"] = _package }
					}));
			}
		}

		// No project asks for it. If it is pinned centrally it can only be reaching the build as a
		// transitive dependency of something else, which this repository cannot simply remove.
		var dirPackages = context.GetFileContent("Directory.Packages.props");
		return Task.FromResult(ReferencesPackage(dirPackages, _package)
			? NotApplicable(
				$"{_package} is pinned in Directory.Packages.props but not referenced by any project, "
				+ "so it is a transitive dependency rather than a choice made by this repository.")
			: Pass($"No {_package} references found."));
	}
}
