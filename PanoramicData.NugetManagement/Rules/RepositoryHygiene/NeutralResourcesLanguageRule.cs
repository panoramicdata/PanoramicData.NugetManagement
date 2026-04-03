using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that NeutralResourcesLanguage is set.
/// </summary>
public class NeutralResourcesLanguageRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "REPO-03";

	/// <inheritdoc />
	public override string RuleName => "NeutralResourcesLanguage set";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.RepositoryHygiene;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var csprojFiles = context.FindFiles(".csproj")
			.Where(f => !f.Contains(".Test", StringComparison.OrdinalIgnoreCase))
			.ToList();

		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (content is not null && !Contains(content, "<NeutralResourcesLanguage>"))
			{
				return Task.FromResult(Fail(
					$"{csproj} does not set NeutralResourcesLanguage.",
					new RuleAdvisory
					{
						Summary = "Add <NeutralResourcesLanguage>en</NeutralResourcesLanguage> to the .csproj.",
						Detail = $"The project `{csproj}` does not set `NeutralResourcesLanguage`. Add `<NeutralResourcesLanguage>en</NeutralResourcesLanguage>` to a `<PropertyGroup>`.",
						Data = new()
						{
							["file"] = csproj,
							["remediation_type"] = "ensure_csproj_property",
							["property_name"] = "NeutralResourcesLanguage",
							["property_value"] = "en"
						}
					}));
			}
		}

		return Task.FromResult(Pass("All non-test projects have NeutralResourcesLanguage set."));
	}
}
