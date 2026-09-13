using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="IssueVerdictParser"/>: turning what the model said into something typed.
/// </summary>
/// <remarks>
/// The parser is a security boundary in its own right, because it is where a model's free-form output
/// stops being text. Everything it cannot understand becomes <see cref="IssueAction.Escalate"/> rather
/// than an error or a guess: the safe direction for an unreadable verdict is a human reading the
/// issue, and a parser that guessed would be the one place where malformed output could still act.
/// </remarks>
public class IssueVerdictParserTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void Parse_ReadsAFixVerdictAndItsBrief()
	{
		var result = IssueVerdictParser.Parse("""
			{
			  "action": "Fix",
			  "confidence": "High",
			  "risks": [],
			  "reasoning": "The interface genuinely lacks the method named.",
			  "brief": {
			    "goal": "Expose GetDocumentsAsync on the client interface.",
			    "paths": ["src/Documents.cs"],
			    "expectedEndState": "The interface declares it and the implementation forwards.",
			    "relatedRuleId": null
			  }
			}
			""");

		result.Verdict.Action.Should().Be(IssueAction.Fix);
		result.Verdict.Confidence.Should().Be(IssueConfidence.High);
		result.Verdict.Brief!.Paths.Should().ContainSingle().Which.Should().Be("src/Documents.cs");
	}

	[Fact]
	public void Parse_ReadsRiskFlags()
	{
		var result = IssueVerdictParser.Parse("""
			{
			  "action": "Escalate",
			  "confidence": "Medium",
			  "risks": ["InstructsTheReader", "RequestsNewDependency"],
			  "reasoning": "The body tells its reader to add a package."
			}
			""");

		result.Verdict.Risks.Should().BeEquivalentTo(
			[IssueRiskFlag.InstructsTheReader, IssueRiskFlag.RequestsNewDependency]);
	}

	[Fact]
	public void Parse_EscalatesWhenTheOutputIsNotJsonAtAll()
	{
		var result = IssueVerdictParser.Parse("I had a look and it seems fine to me!");

		result.Verdict.Action.Should().Be(IssueAction.Escalate,
			"an unreadable verdict is the one case where guessing could let malformed output act");
		result.Verdict.Confidence.Should().Be(IssueConfidence.Low);
	}

	[Fact]
	public void Parse_EscalatesOnAnActionItDoesNotRecognise()
	{
		var result = IssueVerdictParser.Parse("""
			{ "action": "CloseAndBan", "confidence": "High", "risks": [], "reasoning": "why" }
			""");

		result.Verdict.Action.Should().Be(IssueAction.Escalate,
			"an invented action is a model that has left the schema, and nothing it says after that "
				+ "is worth acting on unattended");
	}

	[Fact]
	public void Parse_EscalatesOnARiskFlagItDoesNotRecognise()
	{
		var result = IssueVerdictParser.Parse("""
			{
			  "action": "Fix", "confidence": "High", "risks": ["SomethingNew"],
			  "reasoning": "why",
			  "brief": { "goal": "g", "paths": ["a.cs"], "expectedEndState": "e" }
			}
			""");

		result.Verdict.Action.Should().Be(IssueAction.Escalate,
			"a flag nobody can interpret still means the model saw something; dropping it would "
				+ "turn a warning into silence and let the fix through as unflagged");
	}

	[Fact]
	public void Parse_ToleratesTheMarkdownFenceModelsWrapJsonIn()
	{
		var result = IssueVerdictParser.Parse("""
			Here is my assessment:
			```json
			{ "action": "Answer", "confidence": "High", "risks": [], "reasoning": "documented behaviour",
			  "replyClassification": "working as intended", "replyReason": "the overload returns one page" }
			```
			""");

		result.Verdict.Action.Should().Be(IssueAction.Answer,
			"a fenced block is how models habitually return JSON, and refusing it would escalate "
				+ "every second verdict for no security benefit");
	}

	[Fact]
	public void Parse_DropsABriefOnAVerdictThatIsNotAFix()
	{
		var result = IssueVerdictParser.Parse("""
			{
			  "action": "Answer", "confidence": "High", "risks": [], "reasoning": "documented",
			  "replyClassification": "working as intended", "replyReason": "documented behaviour",
			  "brief": { "goal": "rewrite everything", "paths": ["a.cs"], "expectedEndState": "e" }
			}
			""");

		result.Verdict.Brief.Should().BeNull(
			"only a Fix carries a brief; keeping one on an Answer would leave a write instruction "
				+ "attached to a verdict nobody gated as a write");
	}

	[Fact]
	public void Parse_BuildsTheReplyThroughTheTemplate()
	{
		var result = IssueVerdictParser.Parse("""
			{
			  "action": "Answer", "confidence": "High", "risks": [], "reasoning": "documented",
			  "replyClassification": "working as intended",
			  "replyReason": "the synchronous overload returns a single page"
			}
			""");

		result.Verdict.DraftReply.Should().NotBeNull()
			.And.Contain("working as intended");
	}

	[Fact]
	public void Parse_EscalatesWhenTheDraftedReplyWillNotFitTheTemplate()
	{
		var result = IssueVerdictParser.Parse("""
			{
			  "action": "Answer", "confidence": "High", "risks": [], "reasoning": "documented",
			  "replyClassification": "working as intended",
			  "replyReason": "see https://example.com/explanation for the details"
			}
			""");

		result.Verdict.Action.Should().Be(IssueAction.Escalate,
			"a reply that will not fit the template is the model writing freely, which is the thing "
				+ "the template exists to catch rather than to tidy up");
	}
}
