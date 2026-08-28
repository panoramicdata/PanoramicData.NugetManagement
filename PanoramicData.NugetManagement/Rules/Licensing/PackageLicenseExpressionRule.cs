using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that PackageLicenseExpression matches the expected license.
/// </summary>
public class PackageLicenseExpressionRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "LIC-02";

	/// <inheritdoc />
	public override string RuleName => "PackageLicenseExpression matches expected license";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Licensing;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var notApplicable = PackagingCheckApplies(context, out var projects);
		if (notApplicable is not null)
		{
			return Task.FromResult(notApplicable);
		}

		var expected = context.Options.ExpectedLicense;
		var expectedTag = $"<PackageLicenseExpression>{expected}</PackageLicenseExpression>";

		var missing = projects
			.Where(csproj => !HasMsBuildProperty(context.GetFileContent(csproj), "PackageLicenseExpression", expected))
			.ToList();

		if (missing.Count > 0)
		{
			return Task.FromResult(Fail(
				$"{string.Join(", ", missing)}: PackageLicenseExpression does not match expected \"{expected}\".",
				new RuleAdvisory
				{
					Summary = $"Add {expectedTag} to the .csproj.",
					Detail = $"These published projects do not have `PackageLicenseExpression` set to `{expected}`: {string.Join(", ", missing)}. Add `{expectedTag}` to the project file.",
					Data = new()
					{
						["projects"] = missing.ToArray(),
						["expected_license"] = expected,
						["remediation_type"] = "ensure_csproj_property",
						["property_name"] = "PackageLicenseExpression",
						["property_value"] = expected
					}
				}));
		}

		return Task.FromResult(Pass($"All {projects.Count} published project(s) have PackageLicenseExpression = \"{expected}\"."));
	}
}