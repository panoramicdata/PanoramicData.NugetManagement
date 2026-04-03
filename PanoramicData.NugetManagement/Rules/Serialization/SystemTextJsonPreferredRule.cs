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
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var dirPackages = context.GetFileContent("Directory.Packages.props");
		if (Contains(dirPackages, "Newtonsoft.Json"))
		{
			return Task.FromResult(Fail(
				"Newtonsoft.Json is referenced in Directory.Packages.props.",
				new RuleAdvisory
				{
					Summary = "Migrate to System.Text.Json. Remove Newtonsoft.Json references.",
					Detail = "Newtonsoft.Json is referenced in `Directory.Packages.props`. Migrate all serialization code to `System.Text.Json` and remove the Newtonsoft.Json package reference.",
					Data = new() { ["file"] = "Directory.Packages.props", ["package"] = "Newtonsoft.Json" }
				}));
		}

		var csprojFiles = context.FindFiles(".csproj");
		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (Contains(content, "Newtonsoft.Json"))
			{
				return Task.FromResult(Fail(
					$"Newtonsoft.Json is referenced in {csproj}.",
					new RuleAdvisory
					{
						Summary = "Migrate to System.Text.Json. Remove Newtonsoft.Json references.",
						Detail = $"Newtonsoft.Json is referenced in `{csproj}`. Migrate all serialization code to `System.Text.Json` and remove the Newtonsoft.Json package reference.",
						Data = new() { ["file"] = csproj, ["package"] = "Newtonsoft.Json" }
					}));
			}
		}

		return Task.FromResult(Pass("No Newtonsoft.Json references found."));
	}
}
