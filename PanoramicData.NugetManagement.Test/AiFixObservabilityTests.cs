using System.Text.Json;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for what makes an AI fix session cheaper to run and possible to study afterwards: the patch
/// tool, the model being shown its own calls, the per-turn timings, and the on-disk session log.
/// </summary>
public class AiFixObservabilityTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		$"observability-{Guid.NewGuid():n}");

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private static AiToolCall Call(string name, params (string Name, string Value)[] arguments)
		=> new(name, arguments.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal));

	private Task<AiToolResult> ReplaceAsync(string path, string oldText, string newText)
		=> new AiFixToolbox(_root).ExecuteAsync(
			Call("replace_in_file", ("path", path), ("old_text", oldText), ("new_text", newText)),
			TestContext.Current.CancellationToken);

	private string Write(string name, string content)
	{
		Directory.CreateDirectory(_root);
		var path = Path.Combine(_root, name);
		File.WriteAllText(path, content);

		return path;
	}

	[Fact]
	public async Task ReplaceInFile_ReplacesOneExactMatch_AndLeavesTheRestAlone()
	{
		var path = Write("a.txt", "one\ntwo\nthree\n");

		var result = await ReplaceAsync("a.txt", "two", "TWO");

		result.IsError.Should().BeFalse();
		(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Should().Be("one\nTWO\nthree\n");
	}

	[Fact]
	public async Task ReplaceInFile_WhenTheTextIsMissing_ErrorsAndWritesNothing()
	{
		var path = Write("a.txt", "one\n");

		var result = await ReplaceAsync("a.txt", "absent", "x");

		result.IsError.Should().BeTrue();
		result.Content.Should().Contain("not found").And.Contain("write_file");
		(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Should().Be("one\n");
	}

	[Fact]
	public async Task ReplaceInFile_WhenTheTextAppearsTwice_ErrorsRatherThanGuessing()
	{
		var path = Write("a.txt", "x\nx\n");

		var result = await ReplaceAsync("a.txt", "x", "y");

		result.IsError.Should().BeTrue();
		result.Content.Should().Contain("more than once");
		(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Should().Be("x\nx\n");
	}

	/// <summary>
	/// The model only ever writes "\n". A multi-line match against a CRLF file has to succeed anyway, and
	/// the file has to stay CRLF.
	/// </summary>
	[Fact]
	public async Task ReplaceInFile_MatchesMultiLineTextInACrlfFile_AndKeepsItCrlf()
	{
		var path = Write("a.txt", "one\r\ntwo\r\nthree\r\n");

		var result = await ReplaceAsync("a.txt", "one\ntwo", "ONE\nTWO");

		result.IsError.Should().BeFalse();
		(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Should().Be("ONE\r\nTWO\r\nthree\r\n");
	}

	[Fact]
	public async Task ReplaceInFile_RecordsTheFileAsWritten()
	{
		Write("a.txt", "one\n");
		var toolbox = new AiFixToolbox(_root);

		await toolbox.ExecuteAsync(
			Call("replace_in_file", ("path", "a.txt"), ("old_text", "one"), ("new_text", "two")),
			TestContext.Current.CancellationToken);

		toolbox.FilesWritten.Should().Contain("a.txt");
	}

	[Fact]
	public async Task ReplaceInFile_HonoursTheWriteAllowlist()
	{
		Write("a.txt", "one\n");
		Write("b.txt", "one\n");
		var toolbox = new AiFixToolbox(_root, writablePaths: ["b.txt"]);

		var result = await toolbox.ExecuteAsync(
			Call("replace_in_file", ("path", "a.txt"), ("old_text", "one"), ("new_text", "two")),
			TestContext.Current.CancellationToken);

		result.IsError.Should().BeTrue();
		(await File.ReadAllTextAsync(Path.Combine(_root, "a.txt"), TestContext.Current.CancellationToken)).Should().Be("one\n");
	}

	[Fact]
	public void ToolSpecs_DescribeReplaceInFile()
		=> AiFixSession.ToolSpecs.Select(spec => spec.Name).Should().Contain("replace_in_file");

	[Fact]
	public void Metrics_WaitedIsTotalMinusWhatWasWorkedOn()
	{
		var metrics = new AiTurnMetrics(
			PromptTokens: 811,
			PromptTime: TimeSpan.FromSeconds(1.3),
			GeneratedTokens: 42,
			GeneratedTime: TimeSpan.FromSeconds(1.2),
			LoadTime: TimeSpan.Zero,
			TotalTime: TimeSpan.FromSeconds(311));

		metrics.Waited.TotalSeconds.Should().BeApproximately(308.5, 0.01);
		metrics.TokensPerSecond.Should().BeApproximately(35, 0.01);
	}

	private sealed class ScriptedModel(params AiModelTurn[] turns) : IChatModel
	{
		private int _index;

		public List<IReadOnlyList<AiMessage>> Conversations { get; } = [];

		public Task<AiModelTurn> NextAsync(
			string systemPrompt,
			IReadOnlyList<AiMessage> conversation,
			IReadOnlyList<AiToolSpec> tools,
			Action<AiStreamDelta>? onDelta,
			CancellationToken cancellationToken)
		{
			Conversations.Add([.. conversation]);

			return Task.FromResult(_index < turns.Length
				? turns[_index++]
				: new AiModelTurn(null, [Call("finish", ("summary", "done"))]));
		}
	}

	private static AiFixRequest Request() => new("owner/repo", "X-01", "Rule", "Do the thing.", "System.");

	private static Func<CancellationToken, Task<AiRuleCheck>> Passes()
		=> _ => Task.FromResult(new AiRuleCheck(true, "ok"));

	/// <summary>
	/// The history has to carry the model's calls with their arguments. A line saying only "Calling
	/// write_file." left the model unable to see what it had written, which is how a session came to
	/// re-read a file it had just finished writing.
	/// </summary>
	[Fact]
	public async Task RunAsync_ShowsTheModelItsOwnEarlierCalls()
	{
		Directory.CreateDirectory(_root);
		var model = new ScriptedModel(
			new AiModelTurn(null, [Call("write_file", ("path", "n.txt"), ("content", "hello"))]));

		var session = new AiFixSession(
			model, new AiFixToolbox(_root), new AiFixOptions { MaxTurnsPerAttempt = 4, MaxAttempts = 1 }, _ => { });

		await session.RunAsync(Request(), Passes(), TestContext.Current.CancellationToken);

		var secondTurn = model.Conversations[1];
		var assistant = secondTurn.Single(m => m.Role == "assistant");

		assistant.ToolCalls.Should().ContainSingle()
			.Which.Arguments["content"].Should().Be("hello");
	}

	[Fact]
	public async Task RunAsync_ReportsTheTurnsCost_WhenTheServerSaysWhatItWas()
	{
		Directory.CreateDirectory(_root);
		var lines = new List<string>();

		var metrics = new AiTurnMetrics(
			3214, TimeSpan.FromSeconds(5.2), 412, TimeSpan.FromSeconds(12.1), TimeSpan.Zero, TimeSpan.FromSeconds(29.7));

		var model = new ScriptedModel(
			new AiModelTurn(null, [Call("finish", ("summary", "done"))], Metrics: metrics));

		var session = new AiFixSession(
			model, new AiFixToolbox(_root), new AiFixOptions { MaxTurnsPerAttempt = 2, MaxAttempts = 1 }, lines.Add);

		await session.RunAsync(Request(), Passes(), TestContext.Current.CancellationToken);

		lines.Should().Contain(line => line.Contains("waited 12.4s") && line.Contains("3,214 tok"));
	}

	[Fact]
	public async Task RunAsync_TellsTheModelWhichFilesItHasAlreadyChanged_OnARetry()
	{
		Directory.CreateDirectory(_root);
		var model = new ScriptedModel(
			new AiModelTurn(null, [Call("write_file", ("path", "n.txt"), ("content", "x")), Call("finish", ("summary", "s"))]),
			new AiModelTurn(null, [Call("finish", ("summary", "s"))]));

		var checks = 0;
		var session = new AiFixSession(
			model, new AiFixToolbox(_root), new AiFixOptions { MaxTurnsPerAttempt = 4, MaxAttempts = 2 }, _ => { });

		await session.RunAsync(
			Request(),
			_ => Task.FromResult(++checks == 2 ? new AiRuleCheck(true, "ok") : new AiRuleCheck(false, "still wrong")),
			TestContext.Current.CancellationToken);

		model.Conversations[1].Last(m => m.Role == "user").Content
			.Should().Contain("still wrong").And.Contain("n.txt");
	}

	[Fact]
	public async Task RunAsync_WritesALogOfEverythingThatHappened()
	{
		Directory.CreateDirectory(_root);
		var logPath = Path.Combine(_root, "logs", "session.jsonl");

		using (var log = new AiSessionLog(logPath))
		{
			var model = new ScriptedModel(
				new AiModelTurn("narrating", [Call("write_file", ("path", "n.txt"), ("content", "hello"))], Thinking: "hmm"));

			var session = new AiFixSession(
				model,
				new AiFixToolbox(_root),
				new AiFixOptions { MaxTurnsPerAttempt = 4, MaxAttempts = 1 },
				_ => { },
				sessionLog: log);

			await session.RunAsync(Request(), Passes(), TestContext.Current.CancellationToken);
		}

		var events = (await File.ReadAllLinesAsync(logPath, TestContext.Current.CancellationToken))
			.Select(line => JsonDocument.Parse(line).RootElement)
			.ToList();

		events.Select(e => e.GetProperty("type").GetString()).Should().Contain(
			["attempt_start", "turn", "tool_result", "rule_check", "session_end"]);

		var turn = events.First(e => e.GetProperty("type").GetString() == "turn");
		turn.GetProperty("thinking").GetString().Should().Be("hmm");
		turn.GetProperty("tool_calls")[0].GetProperty("arguments").GetProperty("content").GetString().Should().Be("hello");

		events.First(e => e.GetProperty("type").GetString() == "tool_result")
			.GetProperty("content").GetString().Should().Be("Wrote n.txt.");
	}

	[Fact]
	public void SessionLog_None_RecordsNothingAndDoesNotThrow()
	{
		var act = () => AiSessionLog.None.Write("turn", new Dictionary<string, object?>());

		act.Should().NotThrow();
		AiSessionLog.None.Path.Should().BeEmpty();
	}

	[Fact]
	public void LogStore_Purge_DeletesFilesOlderThanRetention_AndKeepsTheRest()
	{
		var store = new AiSessionLogStore(_root, () => 30);

		using (var old = store.Open("owner/repo", "OLD-01"))
		using (var recent = store.Open("owner/repo", "NEW-01"))
		{
			old.Write("x", new Dictionary<string, object?>());
			recent.Write("x", new Dictionary<string, object?>());
		}

		var oldPath = Directory.EnumerateFiles(_root, "*OLD-01.jsonl", SearchOption.AllDirectories).Single();
		File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-31));

		var deleted = store.Purge();

		deleted.Should().Be(1);
		File.Exists(oldPath).Should().BeFalse();
		Directory.EnumerateFiles(_root, "*NEW-01.jsonl", SearchOption.AllDirectories).Should().ContainSingle();
	}

	[Fact]
	public void LogStore_Purge_WithZeroRetention_KeepsEverything()
	{
		var store = new AiSessionLogStore(_root, () => 0);

		using (var log = store.Open("owner/repo", "OLD-01"))
		{
			log.Write("x", new Dictionary<string, object?>());
		}

		var path = Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.AllDirectories).Single();
		File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-400));

		store.Purge().Should().Be(0);
		File.Exists(path).Should().BeTrue();
	}

	[Fact]
	public void LogStore_Open_NamesTheFileAfterTheRepositoryAndRule_WithNoPathCharacters()
	{
		using var log = new AiSessionLogStore(_root, () => 30).Open("panoramic data/Some:Repo", "TST-10");

		Path.GetFileName(log.Path).Should().EndWith("__panoramic_data_Some_Repo__TST-10.jsonl");
	}
}
