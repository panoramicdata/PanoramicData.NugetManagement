using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="IGovernsDependency"/>: which rules claim to enforce a minimum version of a
/// named dependency, and — just as importantly — which do not.
/// </summary>
public class GovernsDependencyTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static IRule Rule(string id) => RuleRegistry.Rules.Single(r => r.RuleId == id);

	private static DependencyRef Action(string name) => new(DependencyEcosystem.GitHubActions, name);

	private static DependencyRef Package(string name) => new(DependencyEcosystem.NuGet, name);

	[Theory]
	[InlineData("CI-05", "actions/checkout")]
	[InlineData("CI-06", "actions/setup-dotnet")]
	[InlineData("CI-08", "actions/upload-artifact")]
	[InlineData("CI-08", "actions/download-artifact")]
	public void ActionVersionRule_GovernsTheActionItEnforces(string ruleId, string action)
		=> Rule(ruleId).Should().BeAssignableTo<IGovernsDependency>()
			.Which.Governs(Action(action)).Should().BeTrue();

	[Fact]
	public void ActionVersionRule_DoesNotGovernAnUnrelatedAction()
		=> ((IGovernsDependency)Rule("CI-05"))
			.Governs(Action("github/codeql-action")).Should().BeFalse();

	[Fact]
	public void ActionVersionRule_DoesNotGovernAPackageOfTheSameName()
		=> ((IGovernsDependency)Rule("CI-05"))
			.Governs(Package("actions/checkout")).Should().BeFalse(
				"the ecosystem is part of the dependency's identity");

	[Theory]
	[InlineData("PKG-05")]
	[InlineData("PKG-06")]
	[InlineData("PKG-07")]
	public void PackageUpdateRule_GovernsAnyNuGetPackage(string ruleId)
	{
		var rule = Rule(ruleId).Should().BeAssignableTo<IGovernsDependency>().Subject;

		rule.Governs(Package("refit")).Should().BeTrue();
		rule.Governs(Action("actions/checkout")).Should().BeFalse(
			"a NuGet update rule cannot move an action's version");
	}

	[Fact]
	public void PresenceOnlyRule_DoesNotGovernAnything()
		=> Rule("COM-04").Should().NotBeAssignableTo<IGovernsDependency>(
			"COM-04 checks that a codeql-action workflow exists, never which version it pins, so it "
			+ "cannot cover a version bump — claiming coverage would swallow the gap");

	[Fact]
	public void EveryGoverningRule_HasARemediationThatCanActOnIt()
	{
		var registry = new RemediationRegistry();

		var governingWithoutRemediation = RuleRegistry.Rules
			.OfType<IGovernsDependency>()
			.Cast<IRule>()
			.Where(rule => registry.Get(rule.RuleId) is null)
			.Select(rule => rule.RuleId)
			.ToList();

		governingWithoutRemediation.Should().BeEmpty(
			"a rule that governs a dependency but has no remediation would report a Dependabot pull "
			+ "request as covered while nothing could actually fix it");
	}
}
