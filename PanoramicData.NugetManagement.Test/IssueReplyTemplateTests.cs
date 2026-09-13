using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="IssueReplyTemplate"/>: the shape of anything this application says in public
/// about somebody else's issue.
/// </summary>
/// <remarks>
/// A reply is the only output of this feature that a stranger can read, which makes it the route by
/// which text crafted into an issue could come back out carrying whatever the analysis saw. The
/// template exists so a reply is assembled from fixed sentences with a few screened slots, rather
/// than written by a model that has just finished reading an attacker's prose.
/// </remarks>
public class IssueReplyTemplateTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void Build_AssemblesAReplyFromCleanSlots()
	{
		var result = IssueReplyTemplate.Build(
			IssueAction.Answer,
			"working as intended",
			"The synchronous overload returns a single page by design",
			"the version you are running and the call you are making");

		result.Accepted.Should().BeTrue();
		result.Reply.Should().Contain("working as intended")
			.And.Contain("The synchronous overload returns a single page by design");
	}

	[Fact]
	public void Build_RefusesASlotCarryingALink()
	{
		var result = IssueReplyTemplate.Build(
			IssueAction.Answer,
			"working as intended",
			"See https://example.com/why for the explanation",
			null);

		result.Accepted.Should().BeFalse(
			"a link in a generated reply is either something the reporter planted or somewhere we "
				+ "are about to send readers, and neither is worth posting unattended");
	}

	[Fact]
	public void Build_RefusesASlotCarryingACodeFence()
	{
		var result = IssueReplyTemplate.Build(
			IssueAction.Answer,
			"working as intended",
			"The relevant code is ```var x = secret;```",
			null);

		result.Accepted.Should().BeFalse(
			"code in a reply is the shape repository contents would take on the way out");
	}

	[Fact]
	public void Build_RefusesASlotCarryingAFilePath()
	{
		var result = IssueReplyTemplate.Build(
			IssueAction.Answer,
			"working as intended",
			"The problem is in src/Internal/Secrets.cs on line forty",
			null);

		result.Accepted.Should().BeFalse(
			"the analysis is given the file list, so a path in a reply is exactly the fact it was "
				+ "starved of contents to protect");
	}

	[Fact]
	public void Build_RefusesAnOverlongSlot()
	{
		var result = IssueReplyTemplate.Build(
			IssueAction.Answer,
			"working as intended",
			new string('x', IssueReplyTemplate.MaxReasonLength + 1),
			null);

		result.Accepted.Should().BeFalse(
			"a slot that has outgrown its cap is no longer a one-line reason, and length is the "
				+ "cheapest measure of a model having started to write freely");
	}

	[Fact]
	public void Build_RejectionSaysWhatCouldNotBeVerifiedRatherThanAccusing()
	{
		var result = IssueReplyTemplate.Build(
			IssueAction.Reject,
			"could not be reproduced",
			"The behaviour described does not match any version we can build",
			"a failing test or the exact package version");

		result.Accepted.Should().BeTrue();
		result.Reply.Should().NotContainAny("attack", "malicious", "suspicious", "spam",
			"a false positive is survivable only if the reply never accused anybody in the first "
				+ "place, and this is the verdict most likely to be wrong about a real person");
		result.Reply.Should().Contain("reply",
			"a rejection that invites a correction leaves the reporter somewhere to go");
	}
}
