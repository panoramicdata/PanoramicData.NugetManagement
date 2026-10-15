using PanoramicData.NugetManagement.Web.Services;
using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="AiSessionChangeWork"/>: the model-facing half of the pipeline.
/// </summary>
public sealed class AiSessionChangeWorkTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _clone = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		$"changework-{Guid.NewGuid():n}");

	private static readonly AiChangeRequest Request = new("o", "r", AiChangeKind.IssueFix, "issue-42-a", "Fix #42", 42, true);

	public void Dispose()
	{
		if (Directory.Exists(_clone))
		{
			Directory.Delete(_clone, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	/// <summary>A model that finishes straight away with a fixed summary, and records what it was asked.</summary>
	private sealed class FinishingModel(string summary) : IChatModel
	{
		public List<string> FirstTasks { get; } = [];

		public Task<AiModelTurn> NextAsync(
			string systemPrompt,
			IReadOnlyList<AiMessage> conversation,
			IReadOnlyList<AiToolSpec> tools,
			Action<AiStreamDelta>? onDelta,
			CancellationToken cancellationToken)
		{
			FirstTasks.Add(conversation[0].Content);

			return Task.FromResult(new AiModelTurn(
				null,
				[new AiToolCall("finish", new Dictionary<string, string> { ["summary"] = summary })]));
		}
	}

	private AiSessionChangeWork Work(
		FinishingModel model,
		bool checksPass = true)
	{
		Directory.CreateDirectory(_clone);

		var session = new AiFixSession(
			model,
			new AiFixToolbox(_clone),
			new AiFixOptions { MaxTurnsPerAttempt = 3, MaxAttempts = 1 },
			_ => { });

		Task<AiRuleCheck> Check(CancellationToken _) => Task.FromResult(new AiRuleCheck(checksPass, "check"));

		return new AiSessionChangeWork(session, "the report", "Fix it.", "system", Check, Check);
	}

	[Fact]
	public async Task WriteRedTestAsync_ReturnsTheFilterTheModelNamed()
	{
		var work = Work(new FinishingModel("Wrote the test.\nFILTER: FullyQualifiedName~ParserTests"));

		var filter = await work.WriteRedTestAsync(Request, null, TestContext.Current.CancellationToken);

		filter.Should().Be("FullyQualifiedName~ParserTests");
	}

	[Fact]
	public async Task WriteRedTestAsync_ReturnsNullWhenTheModelNamedNoFilter()
	{
		var work = Work(new FinishingModel("Wrote a test but forgot to say which."));

		(await work.WriteRedTestAsync(Request, null, TestContext.Current.CancellationToken)).Should().BeNull();
	}

	[Fact]
	public async Task WriteRedTestAsync_ReturnsNullWhenTheTestDoesNotCompile()
	{
		var work = Work(new FinishingModel("FILTER: FullyQualifiedName~ParserTests"), checksPass: false);

		(await work.WriteRedTestAsync(Request, null, TestContext.Current.CancellationToken)).Should().BeNull(
			"a filter for a test that never compiled selects nothing, and the build check is what said so");
	}

	[Fact]
	public async Task WriteRedTestAsync_PutsTheReportAndTheRejectionInFrontOfTheModel()
	{
		var model = new FinishingModel("FILTER: X");

		await Work(model).WriteRedTestAsync(Request, "The test passes on the unfixed code", TestContext.Current.CancellationToken);

		model.FirstTasks.Should().ContainSingle().Which
			.Should().Contain("the report").And.Contain("The test passes on the unfixed code");
	}

	[Fact]
	public async Task FixAsync_TellsTheModelWhichTestMustTurnGreen()
	{
		var model = new FinishingModel("Done.");

		await Work(model).FixAsync(Request, "FullyQualifiedName~ParserTests", TestContext.Current.CancellationToken);

		model.FirstTasks.Should().ContainSingle().Which
			.Should().Contain("Fix it.").And.Contain("FullyQualifiedName~ParserTests").And.Contain("Do not edit that test");
	}

	[Fact]
	public async Task FixAsync_ForARuleFixLeavesTheTaskAlone()
	{
		var model = new FinishingModel("Done.");

		await Work(model).FixAsync(Request, null, TestContext.Current.CancellationToken);

		model.FirstTasks.Should().ContainSingle().Which.Should().Be("Fix it.");
	}
}
