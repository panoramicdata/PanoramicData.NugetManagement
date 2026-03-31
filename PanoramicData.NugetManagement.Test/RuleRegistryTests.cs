using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the RuleRegistry to ensure all rules are discoverable and valid.
/// </summary>
public class RuleRegistryTests
{
	[Fact]
	public void Rules_ShouldContainAtLeastOneRule()
		=> RuleRegistry.Rules.Should().NotBeEmpty();

	[Fact]
	public void Rules_ShouldHaveUniqueIds()
	{
		var ids = RuleRegistry.Rules.Select(r => r.RuleId).ToList();
		ids.Should().OnlyHaveUniqueItems("each rule must have a unique RuleId");
	}

	[Fact]
	public void Rules_ShouldHaveNonEmptyNames()
	{
		foreach (var rule in RuleRegistry.Rules)
		{
			rule.RuleName.Should().NotBeNullOrWhiteSpace($"Rule {rule.RuleId} must have a name");
		}
	}

	[Fact]
	public void Rules_ShouldHaveValidCategories()
	{
		foreach (var rule in RuleRegistry.Rules)
		{
			Enum.IsDefined(rule.Category).Should().BeTrue($"Rule {rule.RuleId} has invalid Category");
		}
	}

	[Fact]
	public void Rules_ShouldHaveValidSeverities()
	{
		foreach (var rule in RuleRegistry.Rules)
		{
			Enum.IsDefined(rule.Severity).Should().BeTrue($"Rule {rule.RuleId} has invalid Severity");
		}
	}

	[Fact]
	public void Rules_ShouldCoverAllExpectedCategories()
	{
		var coveredCategories = RuleRegistry.Rules
			.Select(r => r.Category)
			.Distinct()
			.ToList();

		// Every category in the enum should have at least one rule
		var allCategories = Enum.GetValues<AssessmentCategory>();
		foreach (var category in allCategories)
		{
			coveredCategories.Should().Contain(category, $"Category {category} should have at least one rule");
		}
	}
}
