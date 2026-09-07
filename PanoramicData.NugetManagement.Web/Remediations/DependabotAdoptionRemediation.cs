using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Web.Remediations;

/// <summary>
/// Writes what a Dependabot pull request proposed, through the remediation writers that already
/// exist.
/// </summary>
/// <remarks>
/// A fix whose source is an open pull request rather than a failing rule. Everything about the write
/// itself is unchanged: <c>update_package_versions</c> and <c>replace_regex_in_files</c> do the work,
/// so there is one implementation of "move a package version" and one of "move an action pin", and
/// adoption cannot drift from what the rules do.
/// <para>
/// Deliberately <em>not</em> registered in <see cref="RemediationRegistry"/>, which is why the
/// attribute is there: the registry discovers every <see cref="IRemediation"/> in the assembly by
/// reflection, so opting out has to be explicit. It is keyed by rule id and triage's own coverage
/// predicate reads it, and registering this under an id that is not a rule would put a non-rule into
/// <see cref="RemediationRegistry.RegisteredRuleIds"/> for everything comparing the registry against
/// the rule set to trip over.
/// </para>
/// </remarks>
[UnregisteredRemediation]
public sealed class DependabotAdoptionRemediation : DataDrivenRemediation
{
	/// <summary>
	/// The identifier this reports itself under.
	/// </summary>
	/// <remarks>
	/// No rule owns this. The value exists because <see cref="IRemediation"/> requires one, and it names
	/// what this is rather than imitating a rule id, so anything that did try to key on it fails to
	/// match a real rule rather than shadowing one.
	/// </remarks>
	public const string AdoptionId = "dependabot-adoption";

	/// <inheritdoc />
	public override string RuleId => AdoptionId;

	/// <summary>
	/// Applies an adoption plan to a clone, and reports the files it wrote.
	/// </summary>
	/// <param name="localPath">The root of the cloned repository.</param>
	/// <param name="plan">What to write.</param>
	/// <param name="onOutput">Where progress is announced.</param>
	/// <returns>
	/// The files actually rewritten. Empty means nothing was written — which is the answer the caller
	/// needs before it closes anybody's pull request, because a writer that matched nothing looks
	/// exactly like one that succeeded.
	/// </returns>
	public static IReadOnlyList<string> Adopt(
		string localPath,
		DependabotAdoptionPlan plan,
		Action<string>? onOutput)
	{
		var remediation = new DependabotAdoptionRemediation();
		var applied = new List<string>();

		if (plan.PackageUpdates.Count > 0)
		{
			remediation.Apply(
				localPath,
				Synthesize(new()
				{
					["remediation_type"] = "update_package_versions",
					["updates"] = plan.PackageUpdates.ToArray()
				}),
				applied,
				onOutput);
		}

		if (plan.ActionPatterns.Count > 0)
		{
			remediation.Apply(
				localPath,
				Synthesize(new()
				{
					["remediation_type"] = "replace_regex_in_files",
					["globs"] = ActionUsesPattern.WorkflowGlobs,
					["patterns"] = plan.ActionPatterns.ToArray(),
					["replacements"] = plan.ActionReplacements.ToArray()
				}),
				applied,
				onOutput);
		}

		return applied;
	}

	/// <summary>
	/// A failing result carrying the given advisory data, and nothing else worth reading.
	/// </summary>
	/// <remarks>
	/// <see cref="IRemediation.Apply"/> reads nothing from a result except its advisory data, which is
	/// what lets adoption reuse the writers with no rule behind it and no new interface. The other
	/// properties are required by the model, so they say what this is rather than imitating a rule.
	/// </remarks>
	/// <param name="data">The advisory data the writer will read.</param>
	private static RuleResult Synthesize(Dictionary<string, object> data) => new()
	{
		RuleId = AdoptionId,
		RuleName = "Adopting what a Dependabot pull request proposed",
		Category = AssessmentCategory.NuGetHygiene,
		Severity = AssessmentSeverity.Warning,
		Passed = false,
		Message = "Adopting what a Dependabot pull request proposed.",
		Advisory = new RuleAdvisory
		{
			Summary = "Write the versions the pull request proposed.",
			Detail = "Applied by Dependabot adoption rather than by a failing rule.",
			Data = data
		}
	};
}
