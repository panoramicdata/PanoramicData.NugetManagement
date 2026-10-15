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

	/// <summary>
	/// A failed session must remove only its own work. The file it did not touch, and the edit that was
	/// already in a file before it started, both have to survive.
	/// </summary>
	[Fact]
	public async Task Undo_RestoresWhatTheSessionWrote_AndLeavesEverythingElse()
	{
		var edited = Write("edited.txt", "before\n");
		var untouched = Write("untouched.txt", "someone else's change\n");
		var toolbox = new AiFixToolbox(_root);

		await toolbox.ExecuteAsync(
			Call("replace_in_file", ("path", "edited.txt"), ("old_text", "before"), ("new_text", "after")),
			TestContext.Current.CancellationToken);
		await toolbox.ExecuteAsync(
			Call("write_file", ("path", "created.txt"), ("content", "new")),
			TestContext.Current.CancellationToken);

		toolbox.Undo().Should().Be(2);

		(await File.ReadAllTextAsync(edited, TestContext.Current.CancellationToken)).Should().Be("before\n");
		File.Exists(Path.Combine(_root, "created.txt")).Should().BeFalse();
		(await File.ReadAllTextAsync(untouched, TestContext.Current.CancellationToken)).Should().Be("someone else's change\n");
		toolbox.FilesWritten.Should().BeEmpty();
	}

	[Fact]
	public async Task Undo_RestoresTheOriginalOfAFileWrittenTwice_NotTheIntermediate()
	{
		var path = Write("a.txt", "v0");
		var toolbox = new AiFixToolbox(_root);

		await toolbox.ExecuteAsync(Call("write_file", ("path", "a.txt"), ("content", "v1")), TestContext.Current.CancellationToken);
		await toolbox.ExecuteAsync(Call("write_file", ("path", "a.txt"), ("content", "v2")), TestContext.Current.CancellationToken);

		toolbox.Undo();

		(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Should().Be("v0");
	}

	private Task<AiToolResult> ListAsync(string glob)
		=> new AiFixToolbox(_root).ExecuteAsync(
			Call("list_files", ("glob", glob)),
			TestContext.Current.CancellationToken);

	[Theory]
	[InlineData("**/*.csproj")]
	[InlineData("*.csproj")]
	[InlineData("src/*.csproj")]
	[InlineData("src/**/*.csproj")]
	[InlineData("src\\*.csproj")]
	[InlineData("*/*.csproj")]
	public async Task ListFiles_AcceptsTheGlobsModelsActuallyWrite(string glob)
	{
		Write("root.txt", "x");
		Directory.CreateDirectory(Path.Combine(_root, "src", "deep"));
		File.WriteAllText(Path.Combine(_root, "src", "A.csproj"), "x");
		File.WriteAllText(Path.Combine(_root, "src", "deep", "B.csproj"), "x");

		var result = await ListAsync(glob);

		result.IsError.Should().BeFalse();
		result.Content.Should().Contain("src/A.csproj");
	}

	[Fact]
	public async Task ListFiles_WithAFolderPrefix_ReturnsOnlyThatFolder()
	{
		Directory.CreateDirectory(Path.Combine(_root, "a"));
		Directory.CreateDirectory(Path.Combine(_root, "b"));
		File.WriteAllText(Path.Combine(_root, "a", "X.cs"), "x");
		File.WriteAllText(Path.Combine(_root, "b", "Y.cs"), "x");

		var result = await ListAsync("a/*.cs");

		result.Content.Should().Be("a/X.cs");
	}

	/// <summary>
	/// A malformed argument must come back as a result the model can read. It used to escape as an
	/// IOException and end the whole session.
	/// </summary>
	[Fact]
	public async Task AToolThatThrows_IsAnErrorResult_NotAnException()
	{
		Directory.CreateDirectory(_root);

		var result = await new AiFixToolbox(_root).ExecuteAsync(
			Call("read_file", ("path", "a\0b")),
			TestContext.Current.CancellationToken);

		result.IsError.Should().BeTrue();
		result.Content.Should().Contain("read_file failed");
	}

	/// <summary>
	/// "*/*.csproj" is exactly one folder deep, as in any other glob implementation. It is the pattern
	/// that once killed a session, and matching it must not also sweep in everything below.
	/// </summary>
	[Fact]
	public async Task ListFiles_SingleStarFolder_IsExactlyOneLevelDeep()
	{
		Directory.CreateDirectory(Path.Combine(_root, "src", "deep"));
		File.WriteAllText(Path.Combine(_root, "src", "A.csproj"), "x");
		File.WriteAllText(Path.Combine(_root, "src", "deep", "B.csproj"), "x");
		File.WriteAllText(Path.Combine(_root, "Top.csproj"), "x");

		var result = await ListAsync("*/*.csproj");

		result.Content.Should().Be("src/A.csproj");
	}

	/// <summary>
	/// Ollama reads <c>think</c> from the top level of the request and ignores it inside options. The
	/// body is built here from the client's own model, so this holds the two together: if the library's
	/// property names ever change, the model would stop receiving its tools or its context size without
	/// any error.
	/// </summary>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void DirectBody_CarriesThinkAtTheTopLevel_AlongsideTheRequestTheClientBuilds(bool think)
	{
		var request = new Ollama.Api.Models.ChatRequest
		{
			Model = "qwen3.8:27b",
			Messages = [new() { Role = "user", Content = "hi" }],
			Tools = [.. AiFixSession.ToolSpecs.Select(spec => new Ollama.Api.Models.ChatTool
			{
				Type = Ollama.Api.Models.McpType.Function,
				Function = new Ollama.Api.Models.ChatToolFunction { Name = spec.Name, Description = spec.Description }
			})],
			Options = new Ollama.Api.Models.GenerateOptions { NumCtx = 131_072 }
		};

		var body = JsonDocument.Parse(OllamaChatModel.BuildDirectBody(request, think)).RootElement;

		body.GetProperty("think").GetBoolean().Should().Be(think);
		body.GetProperty("stream").GetBoolean().Should().BeTrue();
		body.GetProperty("model").GetString().Should().Be("qwen3.8:27b");
		body.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be("hi");
		body.GetProperty("tools").GetArrayLength().Should().Be(AiFixSession.ToolSpecs.Count);
		body.GetProperty("options").GetProperty("num_ctx").GetInt32().Should().Be(131_072);
	}

	[Fact]
	public void DirectBody_CarriesAnAssistantsToolCalls()
	{
		var request = new Ollama.Api.Models.ChatRequest
		{
			Model = "m",
			Messages =
			[
				new()
				{
					Role = "assistant",
					Content = string.Empty,
					ToolCalls =
					[
						new Ollama.Api.Models.ChatToolCall
						{
							Function = new Ollama.Api.Models.ChatToolFunctionCall
							{
								Name = "write_file",
								Arguments = new Dictionary<string, object?> { ["path"] = "a.txt" }
							}
						}
					]
				}
			]
		};

		var call = JsonDocument.Parse(OllamaChatModel.BuildDirectBody(request, false)).RootElement
			.GetProperty("messages")[0].GetProperty("tool_calls")[0].GetProperty("function");

		call.GetProperty("name").GetString().Should().Be("write_file");
		call.GetProperty("arguments").GetProperty("path").GetString().Should().Be("a.txt");
	}

	[Fact]
	public void OllamaOptions_ThinkIsOffByDefault_AndSurvivesNormalising()
	{
		new PanoramicData.NugetManagement.Web.Models.OllamaOptions().Think.Should().BeFalse();

		new PanoramicData.NugetManagement.Web.Models.OllamaOptions { Think = true }
			.Normalised().Think.Should().BeTrue();
	}

	/// <summary>
	/// A model emits plain text with <c>\n</c> and no byte-order mark. Writing a file through it must not
	/// change either: the whole-file diff that results is what buried a one-line fix in noise.
	/// </summary>
	[Fact]
	public async Task WriteFile_KeepsTheExistingFilesByteOrderMarkAndLineEndings()
	{
		var path = Path.Combine(_root, "p.csproj");
		Directory.CreateDirectory(_root);
		await File.WriteAllBytesAsync(
			path,
			[.. new byte[] { 0xEF, 0xBB, 0xBF }, .. System.Text.Encoding.UTF8.GetBytes("<a>\r\n</a>\r\n")],
			TestContext.Current.CancellationToken);

		await new AiFixToolbox(_root).ExecuteAsync(
			Call("write_file", ("path", "p.csproj"), ("content", "<a>\n<b/>\n</a>\n")),
			TestContext.Current.CancellationToken);

		var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

		bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
		System.Text.Encoding.UTF8.GetString(bytes).Should().Be("﻿<a>\r\n<b/>\r\n</a>\r\n");
	}

	[Fact]
	public async Task ReplaceInFile_KeepsTheByteOrderMark()
	{
		var path = Path.Combine(_root, "p.csproj");
		Directory.CreateDirectory(_root);
		await File.WriteAllBytesAsync(
			path,
			[.. new byte[] { 0xEF, 0xBB, 0xBF }, .. System.Text.Encoding.UTF8.GetBytes("<a>x</a>")],
			TestContext.Current.CancellationToken);

		await ReplaceAsync("p.csproj", "x", "y");

		(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)).Take(3)
			.Should().Equal(0xEF, 0xBB, 0xBF);
	}

	[Fact]
	public async Task WriteFile_ForANewFile_WritesItAsGiven_WithNoByteOrderMark()
	{
		Directory.CreateDirectory(_root);

		await new AiFixToolbox(_root).ExecuteAsync(
			Call("write_file", ("path", "new.txt"), ("content", "a\nb\n")),
			TestContext.Current.CancellationToken);

		(await File.ReadAllBytesAsync(Path.Combine(_root, "new.txt"), TestContext.Current.CancellationToken))
			.Should().Equal(System.Text.Encoding.UTF8.GetBytes("a\nb\n"));
	}

	[Fact]
	public void CallDetail_ShowsWhatAWritePutsIn_AndNothingForAReadOnlyCall()
	{
		AiFixSession.CallDetail(Call("replace_in_file", ("path", "a.cs"), ("old_text", "x\ny"), ("new_text", "z")))
			.Should().Be("replace in a.cs:\n- x\n- y\n+ z");
		AiFixSession.CallDetail(Call("write_file", ("path", "T.cs"), ("content", new string('a', 500))))
			.Should().StartWith("write T.cs:\n").And.Contain("500 characters");
		AiFixSession.CallDetail(Call("read_file", ("path", "a.cs"))).Should().BeNull();
	}

	[Fact]
	public async Task RunAsync_StreamsWhatTheModelWrote_ToThePane_EvenWithNoThinking()
	{
		Directory.CreateDirectory(_root);
		var model = new ScriptedModel(
			new AiModelTurn(null, [Call("write_file", ("path", "n.txt"), ("content", "hello"))]));
		var shown = new List<AiStreamDelta>();

		var session = new AiFixSession(
			model, new AiFixToolbox(_root), new AiFixOptions { MaxTurnsPerAttempt = 4, MaxAttempts = 1 }, _ => { }, shown.Add);

		await session.RunAsync(Request(), Passes(), TestContext.Current.CancellationToken);

		shown.Should().Contain(delta => delta.Kind == AiDeltaKind.Content && delta.Text.Contains("write n.txt:\nhello"));
	}

	/// <summary>
	/// The pane is a log of what happened; the status line is where the session stands. It has to carry the
	/// attempt, the turn, the clock, whatever the caller adds, and the last thing that came back.
	/// </summary>
	[Fact]
	public async Task RunAsync_ReportsAStatusLine_OfWhereItHasGotTo()
	{
		Directory.CreateDirectory(_root);
		var statuses = new List<string>();

		var model = new ScriptedModel(
			new AiModelTurn(null, [Call("write_file", ("path", "n.txt"), ("content", "x"))]));

		var session = new AiFixSession(
			model,
			new AiFixToolbox(_root),
			new AiFixOptions { MaxTurnsPerAttempt = 6, MaxAttempts = 2, TimeBudget = TimeSpan.FromMinutes(25) },
			_ => { },
			sessionLog: null,
			onStatus: statuses.Add,
			statusDetail: () => "coverage 54.5% of 62%");

		await session.RunAsync(Request(), Passes(), TestContext.Current.CancellationToken);

		statuses.Should().Contain(status => status.StartsWith("attempt 1/2 · turn 1/6 · 0 of 25 min", StringComparison.Ordinal)
			&& status.Contains("coverage 54.5% of 62%") && status.EndsWith("waiting for the model", StringComparison.Ordinal));
		statuses.Should().Contain(status => status.Contains("last: write_file n.txt: Wrote n.txt."));
		statuses.Last().Should().EndWith("checking whether that fixed it");
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

	[Theory]
	[InlineData("XML syntax error on line 3: element <function> closed by </parameter>", true)]
	[InlineData("error parsing tool call: raw='{...' err=unexpected end of JSON input", true)]
	[InlineData("model 'qwen3.8:27b' not found", false)]
	[InlineData("connection refused", false)]
	public void MalformedOutput_IsTold_ApartFromARealFault(string error, bool malformed)
		=> OllamaChatModel.IsMalformedOutput(error).Should().Be(malformed);

	private sealed class FlakyModel : IChatModel
	{
		private int _calls;

		public List<IReadOnlyList<AiMessage>> Conversations { get; } = [];

		public Task<AiModelTurn> NextAsync(
			string systemPrompt,
			IReadOnlyList<AiMessage> conversation,
			IReadOnlyList<AiToolSpec> tools,
			Action<AiStreamDelta>? onDelta,
			CancellationToken cancellationToken)
		{
			Conversations.Add([.. conversation]);

			return _calls++ == 0
				? throw new AiModelOutputException("XML syntax error on line 3")
				: Task.FromResult(new AiModelTurn(null, [Call("finish", ("summary", "done"))]));
		}
	}

	/// <summary>
	/// A garbled tool call is the model's mistake, and it costs a turn rather than the session. It used
	/// to escape as an exception and end the run with the model never told.
	/// </summary>
	[Fact]
	public async Task RunAsync_ASessionSurvivesOneMalformedToolCall_AndTellsTheModel()
	{
		Directory.CreateDirectory(_root);
		var model = new FlakyModel();
		var lines = new List<string>();

		var session = new AiFixSession(
			model, new AiFixToolbox(_root), new AiFixOptions { MaxTurnsPerAttempt = 4, MaxAttempts = 1 }, lines.Add);

		var outcome = await session.RunAsync(Request(), Passes(), TestContext.Current.CancellationToken);

		outcome.Succeeded.Should().BeTrue();
		model.Conversations[1].Last().Content.Should().Contain("malformed");
		lines.Should().Contain(line => line.Contains("could not be read"));
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
