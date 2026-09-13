using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="IssueAnalysisSession"/>: reading a stranger's prose with nothing to act with.
/// </summary>
/// <remarks>
/// The session is the quarantine. It reads the one input in this application that anybody on the
/// internet can write, and the reason that is safe is that it holds no tools at all — it cannot read
/// the clone, reach GitHub or touch the network, so there is no instruction an issue body could carry
/// that would cause any of those. That property is asserted here rather than described in a prompt.
/// </remarks>
public class IssueAnalysisSessionTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private sealed class ScriptedModel(string response) : IChatModel
	{
		public List<IReadOnlyList<AiToolSpec>> ToolsOffered { get; } = [];

		public List<string> SystemPrompts { get; } = [];

		public List<IReadOnlyList<AiMessage>> Conversations { get; } = [];

		public Task<AiModelTurn> NextAsync(
			string systemPrompt,
			IReadOnlyList<AiMessage> conversation,
			IReadOnlyList<AiToolSpec> tools,
			Action<AiStreamDelta>? onDelta,
			CancellationToken cancellationToken)
		{
			ToolsOffered.Add(tools);
			SystemPrompts.Add(systemPrompt);
			Conversations.Add([.. conversation]);

			return Task.FromResult(new AiModelTurn(response, []));
		}
	}

	private static readonly DateTimeOffset Now = new(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);

	private static IssueAnalysisInput Input(string? body = "The client is missing GetDocumentsAsync.")
		=> new(
			"panoramicdata/OpenProject.Api",
			1,
			"Missing methods",
			body,
			"ismail-ozturk",
			"None",
			new DateTimeOffset(2025, 3, 10, 0, 0, 0, TimeSpan.Zero),
			null,
			[],
			["src/Documents.cs", "src/OpenProjectClient.cs"],
			["CI-11", "CQ-05"]);

	private static IssueAnalysisSession NewSession(IChatModel model)
		=> new(model, _ => { });

	[Fact]
	public async Task AnalyseAsync_OffersTheModelNoToolsAtAll()
	{
		var model = new ScriptedModel("""{ "action": "Escalate", "confidence": "Low", "risks": [], "reasoning": "unclear" }""");

		await NewSession(model).AnalyseAsync(Input(), Now, TestContext.Current.CancellationToken);

		model.ToolsOffered.Should().OnlyContain(tools => tools.Count == 0,
			"this is the structural claim the whole design rests on: a model reading text anybody "
				+ "can write must have nothing to act with, so no instruction in that text can reach "
				+ "the clone, GitHub or the network");
	}

	[Fact]
	public async Task AnalyseAsync_ReturnsTheParsedVerdict()
	{
		var model = new ScriptedModel("""
			{
			  "action": "Fix", "confidence": "High", "risks": [], "reasoning": "genuinely absent",
			  "brief": { "goal": "add it", "paths": ["src/Documents.cs"], "expectedEndState": "present" }
			}
			""");

		var verdict = await NewSession(model).AnalyseAsync(Input(), Now, TestContext.Current.CancellationToken);

		verdict.Action.Should().Be(IssueAction.Fix);
		verdict.Brief!.Paths.Should().ContainSingle();
	}

	[Fact]
	public async Task AnalyseAsync_EscalatesWhenTheModelJustTalks()
	{
		var model = new ScriptedModel("Looks legitimate to me, I would just fix it.");

		var verdict = await NewSession(model).AnalyseAsync(Input(), Now, TestContext.Current.CancellationToken);

		verdict.Action.Should().Be(IssueAction.Escalate,
			"a model that will not answer in the schema has not been understood, and the safe "
				+ "direction for output nobody can read is a human reading the issue");
	}

	[Fact]
	public async Task AnalyseAsync_MarksTheReportersTextAsUntrusted()
	{
		var model = new ScriptedModel("""{ "action": "Escalate", "confidence": "Low", "risks": [], "reasoning": "x" }""");

		await NewSession(model).AnalyseAsync(Input(), Now, TestContext.Current.CancellationToken);

		var task = model.Conversations[0][0].Content;

		task.Should().Contain("UNTRUSTED",
			"the model is told which part of its input is data rather than instruction — belt to "
				+ "the braces of it holding no tools, not a substitute for them");
		task.Should().Contain("The client is missing GetDocumentsAsync.");
	}

	[Fact]
	public async Task AnalyseAsync_TellsTheModelHowLongTheIssueHasBeenWaiting()
	{
		var model = new ScriptedModel("""{ "action": "Escalate", "confidence": "Low", "risks": [], "reasoning": "x" }""");

		await NewSession(model).AnalyseAsync(Input(), Now, TestContext.Current.CancellationToken);

		model.Conversations[0][0].Content.Should().Contain("552 day",
			"age is a first-class input: a report old enough may describe code that has since been "
				+ "rewritten, though age alone never makes a valid bug invalid");
	}

	[Fact]
	public async Task AnalyseAsync_GivesTheModelTheFileListAndTheRuleIds()
	{
		var model = new ScriptedModel("""{ "action": "Escalate", "confidence": "Low", "risks": [], "reasoning": "x" }""");

		await NewSession(model).AnalyseAsync(Input(), Now, TestContext.Current.CancellationToken);

		var task = model.Conversations[0][0].Content;

		task.Should().Contain("src/Documents.cs").And.Contain("CI-11",
			"paths and rule ids are enough to judge whether a report names things that exist, which "
				+ "is why the analysis never needs file contents to do its job");
	}

	[Fact]
	public async Task AnalyseAsync_CopesWithAnIssueThatHasNoBody()
	{
		var model = new ScriptedModel("""{ "action": "Escalate", "confidence": "Low", "risks": [], "reasoning": "x" }""");

		var verdict = await NewSession(model)
			.AnalyseAsync(Input(body: null), Now, TestContext.Current.CancellationToken);

		verdict.Action.Should().Be(IssueAction.Escalate);
	}
}
