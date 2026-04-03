using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that Authors and Company are set in Directory.Build.props.
/// </summary>
public class AuthorsAndCompanyRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "META-03";

	/// <inheritdoc />
	public override string RuleName => "Authors and Company in Directory.Build.props";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.ProjectMetadata;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var expected = context.Options.ExpectedCopyrightHolder;
		var content = context.GetFileContent("Directory.Build.props");
		if (content is null)
		{
			return Task.FromResult(Fail(
				"Directory.Build.props not found.",
				new RuleAdvisory
				{
					Summary = "Create Directory.Build.props with <Authors> and <Company>.",
					Detail = $"No `Directory.Build.props` file was found. Create one with `<Authors>{expected}</Authors>` and `<Company>{expected}</Company>`.",
					Data = new()
					{
						["file"] = "Directory.Build.props",
						["expected_holder"] = expected,
						["remediation_type"] = "ensure_xml_property",
						["property_name"] = "Authors",
						["property_value"] = expected
					}
				}));
		}

		var hasAuthors = Contains(content, "<Authors>");
		var hasCompany = Contains(content, "<Company>");

		return Task.FromResult(hasAuthors && hasCompany
			? Pass("Authors and Company are set in Directory.Build.props.")
			: Fail(
				"Directory.Build.props is missing <Authors> and/or <Company>.",
				new RuleAdvisory
				{
					Summary = $"Add <Authors>{expected}</Authors> and <Company>{expected}</Company>.",
					Detail = $"`Directory.Build.props` is missing `<Authors>` and/or `<Company>`. Add `<Authors>{expected}</Authors>` and `<Company>{expected}</Company>` to a `<PropertyGroup>`.",
					Data = new()
					{
						["file"] = "Directory.Build.props",
						["expected_holder"] = expected,
						["remediation_type"] = "ensure_xml_property",
						["property_name"] = "Authors",
						["property_value"] = expected
					}
				}));
	}
}
