using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="AiFixSession"/>: the loop that lets a weak model keep trying until the rule
/// itself says it succeeded, and stops it running away.
/// </summary>
public class AiFixSessionTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _clone = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		$"session-{Guid.NewGuid():n}");

	private readonly List<string> _log = [];

	public void Dispose()
	{
		if (Directory.Exists(_clone))
		{
			Directory.Delete(_clone, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	/// <summary>A model that replays a script, and records what it was asked.</summary>
	private sealed class ScriptedModel(params AiModelTurn[] turns) : IChatModel
	{
		private int _index;

		public List<string> SystemPrompts { get; } = [];

		public List<IReadOnlyList<AiMessage>> Conversations { get; } = [];

		public int Calls => _index;

		public Task<AiModelTurn> NextAsync(
			string systemPrompt,
			IReadOnlyList<AiMessage> conversation,
			IReadOnlyList<AiToolSpec> tools,
			Action<AiStreamDelta>? onDelta,
			CancellationToken cancellationToken)
		{
			SystemPrompts.Add(systemPrompt);
			Conversations.Add([.. conversation]);

			// Running off the end means the script did not anticipate a turn: keep finishing rather than
			// throwing, so a test failure reads as "the loop did something unexpected" and not as an
			// index error.
			var turn = _index < turns.Length
				? turns[_index]
				: new AiModelTurn(null, [Call("finish", ("summary", "script exhausted"))]);

			_index++;
			return Task.FromResult(turn);
		}
	}

	/// <summary>
	/// A model that emits a scripted set of stream deltas before answering, and records whether it was
	/// given anywhere to send them.
	/// </summary>
	private sealed class StreamingModel(params AiStreamDelta[] deltas) : IChatModel
	{
		public bool WasGivenASink { get; private set; }

		public Task<AiModelTurn> NextAsync(
			string systemPrompt,
			IReadOnlyList<AiMessage> conversation,
			IReadOnlyList<AiToolSpec> tools,
			Action<AiStreamDelta>? onDelta,
			CancellationToken cancellationToken)
		{
			WasGivenASink = onDelta is not null;

			foreach (var delta in deltas)
			{
				onDelta?.Invoke(delta);
			}

			return Task.FromResult(new AiModelTurn(null, [Call("finish", ("summary", "done"))]));
		}
	}

	/// <summary>
	/// The session has to hand the model somewhere to stream to, and pass what arrives straight out.
	/// Without this the pane can only show a session after it has finished, which for a 27b model
	/// grinding through an agentic loop is most of the reason to watch it at all.
	/// </summary>
	[Fact]
	public async Task RunAsync_ForwardsTheModelsStreamedDeltas()
	{
		var model = new StreamingModel(
			new AiStreamDelta(AiDeltaKind.Thinking, "the rule wants a SECURITY.md"),
			new AiStreamDelta(AiDeltaKind.Content, "writing it now"));

		var streamed = new List<AiStreamDelta>();
		var session = NewSession(model, onDelta: streamed.Add);

		await session.RunAsync(Request(), PassesOnAttempt(1), TestContext.Current.CancellationToken);

		model.WasGivenASink.Should().BeTrue("a session that keeps the sink to itself streams nowhere");
		streamed.Should().Equal([
			new AiStreamDelta(AiDeltaKind.Thinking, "the rule wants a SECURITY.md"),
			new AiStreamDelta(AiDeltaKind.Content, "writing it now")]);
	}

	/// <summary>
	/// A session with nowhere to stream must still run. Every existing caller passes no sink.
	/// </summary>
	[Fact]
	public async Task RunAsync_WithNoSink_StillRuns()
	{
		var session = NewSession(new ScriptedModel(Turn(Call("finish", ("summary", "done")))));

		var outcome = await session.RunAsync(Request(), PassesOnAttempt(1), TestContext.Current.CancellationToken);

		outcome.Succeeded.Should().BeTrue();
	}

	private static AiToolCall Call(string name, params (string Name, string Value)[] arguments)
		=> new(name, arguments.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal));

	private static AiModelTurn Turn(params AiToolCall[] calls) => new(null, calls);

	private AiFixSession NewSession(
		IChatModel model,
		AiFixOptions? options = null,
		Action<AiStreamDelta>? onDelta = null)
	{
		Directory.CreateDirectory(_clone);

		return new AiFixSession(
			model,
			new AiFixToolbox(_clone),
			options ?? new AiFixOptions { MaxTurnsPerAttempt = 6, MaxAttempts = 3 },
			_log.Add,
			onDelta);
	}

	private static AiFixRequest Request() => new(
		"panoramicdata/Sample",
		"COM-01",
		"SECURITY.md exists",
		"Add a SECURITY.md at the repository root.",
		"System prompt for the test.");

	/// <summary>A rule check that passes on the given attempt and fails before it.</summary>
	private static Func<CancellationToken, Task<AiRuleCheck>> PassesOnAttempt(int attempt)
	{
		var checks = 0;

		return _ =>
		{
			checks++;
			return Task.FromResult(checks >= attempt
				? new AiRuleCheck(true, "SECURITY.md exists.")
				: new AiRuleCheck(false, "SECURITY.md is missing."));
		};
	}

	[Fact]
	public async Task ModelWritesTheFileAndFinishes_AndTheRuleAgrees()
	{
		var model = new ScriptedModel(
			Turn(Call("write_file", ("path", "SECURITY.md"), ("content", "# Security\n"))),
			Turn(Call("finish", ("summary", "Added SECURITY.md"))));

		var outcome = await NewSession(model)
			.RunAsync(Request(), PassesOnAttempt(1), TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		outcome.Succeeded.Should().BeTrue();
		outcome.Attempts.Should().Be(1);
		File.Exists(Path.Combine(_clone, "SECURITY.md")).Should().BeTrue();
	}

	[Fact]
	public async Task TheRuleStillFailing_StartsAnotherAttemptWithTheFailureFedBack()
	{
		var model = new ScriptedModel(
			Turn(Call("finish", ("summary", "I think I am done"))),
			Turn(Call("write_file", ("path", "SECURITY.md"), ("content", "# Security\n"))),
			Turn(Call("finish", ("summary", "Actually done now"))));

		var outcome = await NewSession(model)
			.RunAsync(Request(), PassesOnAttempt(2), TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		outcome.Succeeded.Should().BeTrue();
		outcome.Attempts.Should().Be(2);

		model.Conversations[^1].Should().Contain(
			m => m.Content.Contains("SECURITY.md is missing", StringComparison.Ordinal),
			"the rule's own failure message is the most useful correction available");
	}

	[Fact]
	public async Task TheRuleNeverPassing_GivesUpAfterTheAttemptLimit()
	{
		var model = new ScriptedModel(Turn(Call("finish", ("summary", "done"))));

		var outcome = await NewSession(model, new AiFixOptions { MaxTurnsPerAttempt = 4, MaxAttempts = 2 })
			.RunAsync(
				Request(),
				_ => Task.FromResult(new AiRuleCheck(false, "Still missing.")),
				TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		outcome.Succeeded.Should().BeFalse();
		outcome.Attempts.Should().Be(2, "it must stop rather than burn the GPU for ever");
	}

	[Fact]
	public async Task AModelThatNeverFinishes_IsStoppedByTheTurnLimit()
	{
		// Reads the same file for ever, never calling finish.
		var reads = Enumerable.Repeat(Turn(Call("list_files")), 50).ToArray();
		var model = new ScriptedModel(reads);

		var outcome = await NewSession(model, new AiFixOptions { MaxTurnsPerAttempt = 3, MaxAttempts = 1 })
			.RunAsync(
				Request(),
				_ => Task.FromResult(new AiRuleCheck(false, "Still missing.")),
				TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		outcome.Succeeded.Should().BeFalse();
		model.Calls.Should().Be(3, "the turn limit is per attempt and must be obeyed exactly");
	}

	[Fact]
	public async Task AToolErrorIsFedBack_SoTheModelCanCorrectItself()
	{
		var model = new ScriptedModel(
			Turn(Call("read_file", ("path", "../escape.txt"))),
			Turn(Call("write_file", ("path", "SECURITY.md"), ("content", "# Security\n"))),
			Turn(Call("finish", ("summary", "done"))));

		await NewSession(model)
			.RunAsync(Request(), PassesOnAttempt(1), TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		model.Conversations[1].Should().Contain(
			m => m.Content.Contains("outside the repository", StringComparison.Ordinal));
	}

	[Fact]
	public async Task AModelThatCallsNoTool_IsToldToUseOne_AndDoesNotSpinForEver()
	{
		var model = new ScriptedModel(
			new AiModelTurn("I would suggest adding a security policy.", []),
			Turn(Call("finish", ("summary", "done"))));

		var outcome = await NewSession(model)
			.RunAsync(Request(), PassesOnAttempt(1), TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		outcome.Succeeded.Should().BeTrue();
		model.Conversations[1].Should().Contain(
			m => m.Content.Contains("tool", StringComparison.OrdinalIgnoreCase),
			"prose is not progress, and the model has to be told so");
	}

	[Fact]
	public async Task EveryToolCallAndResult_IsWrittenToTheOutput()
	{
		var model = new ScriptedModel(
			Turn(Call("write_file", ("path", "SECURITY.md"), ("content", "# Security\n"))),
			Turn(Call("finish", ("summary", "Added SECURITY.md"))));

		await NewSession(model)
			.RunAsync(Request(), PassesOnAttempt(1), TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		_log.Should().Contain(line => line.Contains("write_file", StringComparison.Ordinal));
		_log.Should().Contain(line => line.Contains("SECURITY.md", StringComparison.Ordinal));
		_log.Should().Contain(line => line.Contains("finish", StringComparison.Ordinal),
			"the work item's output is the audit trail for what the model did");
	}

	[Fact]
	public async Task TheSystemPromptAndTheTaskBothReachTheModel()
	{
		var model = new ScriptedModel(Turn(Call("finish", ("summary", "done"))));

		await NewSession(model)
			.RunAsync(Request(), PassesOnAttempt(1), TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		model.SystemPrompts[0].Should().Be("System prompt for the test.");
		model.Conversations[0].Should().Contain(
			m => m.Content.Contains("Add a SECURITY.md", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Cancellation_StopsTheLoop()
	{
		using var cancellation = new CancellationTokenSource();
		var model = new ScriptedModel(Enumerable.Repeat(Turn(Call("list_files")), 20).ToArray());

		await cancellation.CancelAsync().ConfigureAwait(true);

		var act = async () => await NewSession(model)
			.RunAsync(Request(), PassesOnAttempt(1), cancellation.Token)
			.ConfigureAwait(true);

		await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);
	}

	[Fact]
	public void TheToolSpecs_DescribeExactlyTheToolsTheToolboxHas()
		=> AiFixSession.ToolSpecs.Select(t => t.Name)
			.Should().BeEquivalentTo(AiFixToolbox.ToolNames);
}
