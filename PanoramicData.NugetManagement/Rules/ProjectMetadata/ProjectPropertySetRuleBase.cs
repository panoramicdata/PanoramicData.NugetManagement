using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// A rule that checks one MSBuild property is set in every packable project.
/// </summary>
public abstract class ProjectPropertySetRuleBase : RuleBase
{
	/// <summary>Gets the name of the MSBuild property that has to be set.</summary>
	protected abstract string PropertyName { get; }

	/// <summary>Gets the one-line advice given when the property is missing.</summary>
	protected abstract string Summary { get; }

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var notApplicable = PackagingCheckApplies(context, out var projects);
		if (notApplicable is not null)
		{
			return Task.FromResult(notApplicable);
		}

		var missing = projects
			.Where(csproj => IsMissing(context, csproj))
			.ToList();

		if (missing.Count == 0)
		{
			return Task.FromResult(Pass(PassMessage(projects.Count)));
		}

		return Task.FromResult(Fail(
			FailureMessage(missing),
			new RuleAdvisory
			{
				Summary = Summary,
				Detail = Detail(missing),
				Data = AdvisoryData(context, missing)
			}));
	}

	/// <summary>Whether <paramref name="csproj"/> still needs fixing. By default, whether the property is unset.</summary>
	/// <param name="context">The repository being assessed.</param>
	/// <param name="csproj">The project to check.</param>
	protected virtual bool IsMissing(RepositoryContext context, string csproj)
		=> !HasMsBuildProperty(context.GetFileContent(csproj), PropertyName);

	/// <summary>The message given when <paramref name="missing"/> projects fail the rule.</summary>
	/// <param name="missing">The projects that fail.</param>
	protected virtual string FailureMessage(List<string> missing)
		=> $"{string.Join(", ", missing)} do(es) not have {PropertyName} set.";

	/// <summary>The message given when every project passes.</summary>
	/// <param name="projectCount">How many projects were checked.</param>
	protected virtual string PassMessage(int projectCount)
		=> $"All {projectCount} published project(s) have {PropertyName} set.";

	/// <summary>Builds the explanation given when <paramref name="missing"/> projects lack the property.</summary>
	/// <param name="missing">The projects without the property.</param>
	protected abstract string Detail(List<string> missing);

	/// <summary>
	/// Builds the data a remediation reads. By default only the projects are listed, which leaves the
	/// fix to a person or a model.
	/// </summary>
	/// <param name="context">The repository being assessed.</param>
	/// <param name="missing">The projects without the property.</param>
	protected virtual Dictionary<string, object> AdvisoryData(RepositoryContext context, List<string> missing)
		=> new() { ["projects"] = missing.ToArray() };

	/// <summary>The standard explanation: the property is missing, and this is how to add it.</summary>
	/// <param name="missing">The projects without the property.</param>
	/// <param name="example">The element to add, for example <c>&lt;PackageId&gt;YourPackageId&lt;/PackageId&gt;</c>.</param>
	protected string StandardDetail(List<string> missing, string example)
		=> $"These published projects do not have `<{PropertyName}>` set: {string.Join(", ", missing)}. Add `{example}` to a `<PropertyGroup>`.";

	/// <summary>The data for a rule whose fix is to set the property to <paramref name="value"/> in every missing project.</summary>
	/// <param name="missing">The projects without the property.</param>
	/// <param name="value">The value to set.</param>
	protected Dictionary<string, object> SetPropertyData(List<string> missing, string value)
		=> new()
		{
			["projects"] = missing.ToArray(),
			["remediation_type"] = "ensure_csproj_property",
			["property_name"] = PropertyName,
			["property_value"] = value
		};
}
