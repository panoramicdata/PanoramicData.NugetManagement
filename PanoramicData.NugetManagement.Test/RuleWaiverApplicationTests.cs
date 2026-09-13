using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests what a waiver does to a rule result. A waiver stops a failure counting against a
/// repository; it does not stop the rule being evaluated, and it does not remove the result from
/// the board. The point is that the decision stays visible, and that the estate can be told when a
/// waiver has outlived the problem it was agreed for.
/// </summary>
public class RuleWaiverApplicationTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void AnUnwaivedResultShouldBeUntouched()
	{
		var result = Failing("HTTP-01");

		RuleWaivers.Apply(result, new RepoOptions()).Should().BeSameAs(result);
	}

	[Fact]
	public void AWaivedFailureShouldStopCountingAsAFailure()
	{
		var waived = RuleWaivers.Apply(Failing("HTTP-01"), OptionsWaiving("HTTP-01", "Talks GraphQL."));

		waived.Passed.Should().BeTrue();
	}

	[Fact]
	public void AWaivedFailureShouldKeepTheVerdictItWouldHaveGiven()
	{
		// Without this the estate can never be told the waiver is no longer needed.
		var waived = RuleWaivers.Apply(Failing("HTTP-01"), OptionsWaiving("HTTP-01", "Talks GraphQL."));

		waived.Waiver.Should().NotBeNull();
		waived.Waiver!.UnderlyingPassed.Should().BeFalse();
		waived.Waiver.UnderlyingMessage.Should().Be("Expected HTTP client package is not referenced.");
	}

	[Fact]
	public void AWaivedResultShouldSayWhyItWasWaived()
	{
		var waived = RuleWaivers.Apply(Failing("HTTP-01"), OptionsWaiving("HTTP-01", "Talks GraphQL."));

		waived.Waiver!.Waiver.Reason.Should().Be("Talks GraphQL.");
		waived.Message.Should().Contain("Talks GraphQL.");
	}

	[Fact]
	public void AWaivedResultShouldOfferNoAdvisory()
	{
		// An advisory is an offer to fix. Nothing should offer to fix what the estate has agreed to
		// leave alone — least of all the AI remediation that reads advisories to find work.
		var waived = RuleWaivers.Apply(Failing("HTTP-01"), OptionsWaiving("HTTP-01", "Talks GraphQL."));

		waived.Advisory.Should().BeNull();
	}

	[Fact]
	public void AWaiverShouldBeStaleOnceTheRulePassesAnyway()
	{
		var waived = RuleWaivers.Apply(Passing("HTTP-01"), OptionsWaiving("HTTP-01", "Talks GraphQL."));

		waived.Waiver!.IsStale.Should().BeTrue("the rule passes on its own, so the waiver can be deleted");
	}

	[Fact]
	public void AWaiverShouldNotBeStaleWhileTheRuleStillFails()
	{
		var waived = RuleWaivers.Apply(Failing("HTTP-01"), OptionsWaiving("HTTP-01", "Talks GraphQL."));

		waived.Waiver!.IsStale.Should().BeFalse();
	}

	[Fact]
	public void ARuleIdShouldMatchRegardlessOfCase()
	{
		// The two assessment paths used to disagree about this, so the same waiver took effect on one
		// and not the other.
		var waived = RuleWaivers.Apply(Failing("HTTP-01"), OptionsWaiving("http-01", "Talks GraphQL."));

		waived.Passed.Should().BeTrue();
	}

	[Fact]
	public void AnObsoleteSuppressionShouldStillWaiveTheRule()
	{
		// SuppressedRules is how a library consumer said this before waivers existed. It keeps working.
		var options = new RepoOptions();
#pragma warning disable CS0618 // Honouring the obsolete member is the point of the test.
		options.SuppressedRules.Add("HTTP-01");
#pragma warning restore CS0618

		RuleWaivers.Apply(Failing("HTTP-01"), options).Passed.Should().BeTrue();
	}

	[Fact]
	public void AnObsoleteSuppressionShouldAdmitItHasNoReason()
	{
		var options = new RepoOptions();
#pragma warning disable CS0618 // Honouring the obsolete member is the point of the test.
		options.SuppressedRules.Add("HTTP-01");
#pragma warning restore CS0618

		var waived = RuleWaivers.Apply(Failing("HTTP-01"), options);

		waived.Waiver!.Waiver.Reason.Should().Contain("no reason");
	}

	private static RuleResult Failing(string ruleId) => new()
	{
		RuleId = ruleId,
		RuleName = "Expected HTTP client package referenced",
		Category = AssessmentCategory.HttpClient,
		Severity = AssessmentSeverity.Info,
		Passed = false,
		Message = "Expected HTTP client package is not referenced.",
		Advisory = new RuleAdvisory
		{
			Summary = "Add a Refit package reference.",
			Detail = "Add a `Refit` package reference and use it for HTTP client interfaces."
		}
	};

	private static RuleResult Passing(string ruleId) => new()
	{
		RuleId = ruleId,
		RuleName = "Expected HTTP client package referenced",
		Category = AssessmentCategory.HttpClient,
		Severity = AssessmentSeverity.Info,
		Passed = true,
		Message = "Expected HTTP client package \"Refit\" is referenced."
	};

	private static RepoOptions OptionsWaiving(string ruleId, string reason) => new()
	{
		Waivers = [new RuleWaiver { RuleId = ruleId, Reason = reason }]
	};
}
