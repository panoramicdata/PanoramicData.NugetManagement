using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that <see cref="RemediationRegistry"/> holds only remediations a rule actually owns.
/// </summary>
/// <remarks>
/// The registry discovers every <see cref="IRemediation"/> in the assembly by reflection, so a new
/// remediation is registered by existing at all. That is right for one that fixes a rule and wrong
/// for one whose source is something else, and nothing caught the difference until adoption arrived.
/// </remarks>
public class RemediationRegistryExclusionTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void EveryRegisteredRuleId_IsARealRule()
	{
		var ruleIds = RuleRegistry.Rules
			.Select(rule => rule.RuleId)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		new RemediationRegistry().RegisteredRuleIds
			.Should().OnlyContain(
				ruleId => ruleIds.Contains(ruleId),
				"the registry is keyed by rule id and is compared against the rule set, so an id that "
					+ "names no rule is something every such comparison has to special-case");
	}

	[Fact]
	public void AdoptionRemediation_IsNotRegistered()
		=> new RemediationRegistry()
			.Get(DependabotAdoptionRemediation.AdoptionId)
			.Should().BeNull(
				"adoption applies what a pull request proposed and has no rule behind it — triage's own "
					+ "coverage predicate reads this registry, and a remediation here answers 'a fix "
					+ "exists' for whatever id it is keyed under");
}
