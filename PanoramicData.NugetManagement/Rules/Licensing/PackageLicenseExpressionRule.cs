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
		if (!context.Options.IsPackable)
		{
			return Task.FromResult(Pass("Repository is not packable — skipping."));
		}

		var expected = context.Options.ExpectedLicense;
		var expectedTag = $"<PackageLicenseExpression>{expected}</PackageLicenseExpression>";

		var csproj = context.FindPrimaryProjectFile();
		if (csproj is null)
		{
			return Task.FromResult(Pass("No primary project found — skipping PackageLicenseExpression check."));
		}

		var content = context.GetFileContent(csproj);
		if (!Contains(content, expectedTag))
		{
			return Task.FromResult(Fail(
				$"{csproj}: PackageLicenseExpression does not match expected \"{expected}\".",
				new RuleAdvisory
				{
					Summary = $"Add {expectedTag} to the .csproj.",
					Detail = $"The project `{csproj}` does not have `PackageLicenseExpression` set to `{expected}`. Add `{expectedTag}` to the project file.",
					Data = new()
					{
						["file"] = csproj,
						["expected_license"] = expected,
						["remediation_type"] = "ensure_csproj_property",
						["property_name"] = "PackageLicenseExpression",
						["property_value"] = expected
					}
				}));
		}

		return Task.FromResult(Pass($"Primary project has