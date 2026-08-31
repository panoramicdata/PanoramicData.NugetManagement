using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Remediations;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests over the playbooks that ship: that each is for a rule the AI path is actually responsible
/// for, and that each says enough to be worth having.
/// </summary>
public class AiPlaybookContentTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly AiPlaybookRegistry _registry = new();

	[Fact]
	public void APlaybookExistsForTheMetadataRules()
		=> _registry.RuleIds.Should().Contain(["META-04", "META-05"],
			"a csproj property with a known value is the most tractable thing a small model can be asked to do");

	/// <summary>
	/// Fix and Fix with AI are disjoint: Fix does what a remediation can do, Fix with AI does what
	/// nothing else can. A playbook for a rule that already has a remediation would put the same work
	/// behind two buttons and make which one to press a matter of guesswork.
	/// </summary>
	[Fact]
	public void NoPlaybookDuplicatesADeterministicRemediation()
	{
		var remediations = new RemediationRegistry();

		var overlapping = _registry.RuleIds
			.Where(ruleId => remediations.Get(ruleId) is not null)
			.ToList();

		overlapping.Should().BeEmpty(
			"a rule with a remediation belongs to Fix, and the two buttons must not overlap");
	}

	[Fact]
	public void EveryPlaybookSaysAllFourThings()
	{
		foreach (var ruleId in _registry.RuleIds)
		{
			var playbook = _registry.For(ruleId)!;

			playbook.Goal.Should().NotBeNullOrWhiteSpace($"{ruleId} needs a goal");
			playbook.ExpectedEndState.Should().NotBeNullOrWhiteSpace($"{ruleId} needs a success criterion");
			playbook.WorkedExample.Should().NotBeNullOrWhiteSpace($"{ruleId} needs an example to copy");
			playbook.Files.Should().NotBeEmpty($"{ruleId} must name where to look, or the model will explore");
		}
	}

	[Fact]
	public void EveryPlaybooksGoalIsOneShortImperativeSentence()
	{
		foreach (var ruleId in _registry.RuleIds)
		{
			var goal = _registry.For(ruleId)!.Goal;

			goal.Should().NotContain("\n", $"{ruleId}'s goal is meant to be one line");
			goal.Length.Should().BeLessThan(200, $"{ruleId}'s goal is an instruction, not an explanation");
		}
	}

	[Fact]
	public void EveryPlaybookNamesARuleThatIsActuallyFailable()
	{
		var known = RuleRegistry.Rules.Select(r => r.RuleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

		_registry.RuleIds.Should().OnlyContain(id => known.Contains(id));
	}
}
