using Microsoft.Extensions.Logging.Abstractions;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;
using PanoramicData.NugetManagement.Web.Services.Governance;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for what the executors write to the governance ledger, and for the origin that says who asked.
/// </summary>
public class GovernanceRecorderTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		$"recorder-{Guid.NewGuid():n}");

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			// Git marks its object files read-only.
			foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
			{
				File.SetAttributes(file, FileAttributes.Normal);
			}

			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private (GovernanceLedger Ledger, GovernanceRecorder Recorder, ReviewPacketStore Packets) Open()
	{
		var ledger = new GovernanceLedger(Path.Combine(_root, "ledger"));
		var packets = new ReviewPacketStore(Path.Combine(_root, "ledger", "packets"));

		return (ledger, new GovernanceRecorder(ledger, packets, NullLogger<GovernanceRecorder>.Instance), packets);
	}

	private static WorkItem Item(string? queuedBy = null) => new()
	{
		Id = "7",
		Title = "Fix TST-10 with AI in Sample",
		Descriptor = WorkDescriptor.ForRepository(WorkKind.FixWithAiRule, "o", "o/Sample", ("ruleId", "TST-10")),
		DedupKey = "k",
		QueuedBy = queuedBy
	};

	private static AiFixRequest Request() => new("o/Sample", "TST-10", "Coverage", "Raise coverage.", "System prompt.");

	private async Task<string> InitRepoAsync()
	{
		var path = Path.Combine(_root, "clone");
		Directory.CreateDirectory(path);

		foreach (var arguments in new[]
		{
			new[] { "init", "-q", "-b", "main" },
			["config", "user.email", "t@example.com"],
			["config", "user.name", "Test"],
			["config", "commit.gpgsign", "false"],
			["config", "core.autocrlf", "false"]
		})
		{
			await LocalRepoService.RunCommandAsync(path, "git", arguments, TestContext.Current.CancellationToken).ConfigureAwait(true);
		}

		await File.WriteAllTextAsync(Path.Combine(path, "tracked.txt"), "one\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
		await LocalRepoService.RunCommandAsync(path, "git", ["add", "-A"], TestContext.Current.CancellationToken).ConfigureAwait(true);
		await LocalRepoService.RunCommandAsync(path, "git", ["commit", "-q", "-m", "first"], TestContext.Current.CancellationToken).ConfigureAwait(true);

		return path;
	}

	[Fact]
	public void TheOrigin_IsNull_UntilSomeoneSetsIt_AndRestoredAfterwards()
	{
		QueueOrigin.Current.Should().BeNull();

		using (QueueOrigin.Use("agent-api"))
		{
			QueueOrigin.Current.Should().Be("agent-api");

			using (QueueOrigin.Use("autopilot"))
			{
				QueueOrigin.Current.Should().Be("autopilot");
			}

			QueueOrigin.Current.Should().Be("agent-api");
		}

		QueueOrigin.Current.Should().BeNull();
	}

	[Fact]
	public void WorkQueuedInsideAnOrigin_RemembersWhoQueuedIt()
	{
		var lanes = new WorkLaneService();
		WorkItem? api;

		using (QueueOrigin.Use("agent-api"))
		{
			api = lanes.Enqueue("a", WorkDescriptor.ForRepository(WorkKind.Build, "o", "o/A"), "ka", null, null);
		}

		var person = lanes.Enqueue("b", WorkDescriptor.ForRepository(WorkKind.Build, "o", "o/B"), "kb", null, null);

		api!.QueuedBy.Should().Be("agent-api");
		person!.QueuedBy.Should().BeNull();
		GovernanceRecorder.ActorFor(api).Should().Be("agent-api");
		GovernanceRecorder.ActorFor(person).Should().Be("user");
	}

	[Fact]
	public void RecordAiSession_WritesAnEntry_WithAPacketThatHoldsTheEvidence()
	{
		var (ledger, recorder, packets) = Open();
		var logPath = Path.Combine(_root, "session.jsonl");
		Directory.CreateDirectory(_root);
		File.WriteAllLines(logPath,
		[
			"""{"at":"2026-10-15T10:00:00Z","type":"session_start","model":"qwen3.8:27b"}""",
			"""{"at":"2026-10-15T10:00:05Z","type":"turn","turn":1,"text":"hello","tool_calls":[{"name":"write_file"}]}""",
			"""{"at":"2026-10-15T10:00:06Z","type":"tool_result","name":"write_file","content":"Wrote a.cs."}""",
			"""{"at":"2026-10-15T10:00:07Z","type":"session_end","succeeded":true}"""
		]);

		recorder.RecordAiSession(
			Item("autopilot"),
			Request(),
			"qwen3.8:27b",
			new AiFixOutcome(true, 1, "fixed"),
			"applied",
			["a.cs"],
			"diff --git a/a.cs b/a.cs",
			logPath);

		var entry = ledger.Query(kind: "ai_session").Single();
		entry.Actor.Should().Be("autopilot");
		entry.Repository.Should().Be("o/Sample");
		entry.RuleId.Should().Be("TST-10");
		entry.Data.GetProperty("model").GetString().Should().Be("qwen3.8:27b");
		entry.Data.GetProperty("promptVersion").GetString().Should().Be(AiFixPrompt.Version);
		entry.Data.GetProperty("turns").GetInt32().Should().Be(1);
		entry.Data.GetProperty("disposition").GetString().Should().Be("applied");
		entry.Data.GetProperty("succeeded").GetBoolean().Should().BeTrue();

		var reference = entry.Data.GetProperty("packet");
		var packet = packets.Read(new PacketReference(
			reference.GetProperty("relativePath").GetString()!,
			reference.GetProperty("sha256").GetString()!,
			0))!.Value;

		packet.GetProperty("task").GetString().Should().Be("Raise coverage.");
		packet.GetProperty("diff").GetString().Should().Contain("diff --git");
		packet.GetProperty("events").GetArrayLength().Should().Be(4);
	}

	[Fact]
	public void RecordAiSession_CutsBulkyToolResults_SoAPacketStaysReadable()
	{
		var logPath = Path.Combine(_root, "session.jsonl");
		Directory.CreateDirectory(_root);
		File.WriteAllLines(logPath,
		[
			$$"""{"type":"tool_result","name":"read_file","content":"{{new string('x', 50_000)}}"}"""
		]);

		var events = GovernanceRecorder.ReadSessionEvents(logPath);

		var content = events.Single()!["content"]!.GetValue<string>();
		content.Length.Should().BeLessThan(5_000);
		content.Should().Contain("more characters");
	}

	[Fact]
	public void RecordAiSession_WithNoUsableSessionLog_StillWritesTheEntry()
	{
		var (ledger, recorder, _) = Open();
		var logPath = Path.Combine(_root, "session.jsonl");
		Directory.CreateDirectory(_root);

		File.WriteAllText(logPath, "{\"type\":\"turn\",\"text\":\"fine\"}\n");

		recorder.RecordAiSession(Item(), Request(), "m", null, "reverted", [], string.Empty, logPath);

		ledger.Query(kind: "ai_session").Should().ContainSingle()
			.Which.Data.GetProperty("disposition").GetString().Should().Be("reverted");
	}

	[Fact]
	public void RecordAiSession_NeverLetsASecretIntoThePacket()
	{
		var (ledger, recorder, packets) = Open();

		recorder.RecordAiSession(
			Item(),
			Request(),
			"m",
			new AiFixOutcome(false, 1, "no"),
			"reverted",
			["a.cs"],
			"+ var request = \"GET /x?api-key=69ae5732fe30d615851e4549e6b7151c56fe51f8\";",
			null);

		var reference = ledger.Query(kind: "ai_session").Single().Data.GetProperty("packet");
		var packet = packets.Read(new PacketReference(
			reference.GetProperty("relativePath").GetString()!,
			reference.GetProperty("sha256").GetString()!,
			0))!.Value;

		packet.GetProperty("diff").GetString().Should().NotContain("69ae5732");
	}

	[Fact]
	public void RecordRemediation_ListsWhatWasApplied()
	{
		var (ledger, recorder, _) = Open();

		recorder.RecordRemediation(Item(), "o/Sample", "META-05", ["A/A.csproj", "A/Logo.png"]);

		var entry = ledger.Query(kind: "remediation_applied").Single();
		entry.RuleId.Should().Be("META-05");
		entry.Data.GetProperty("count").GetInt32().Should().Be(2);
		entry.Data.GetProperty("applied")[1].GetString().Should().Be("A/Logo.png");
	}

	[Fact]
	public void RecordPublish_RecordsTheVersionAndHowGreenTheRepositoryWas()
	{
		var (ledger, recorder, _) = Open();

		recorder.RecordPublish(Item("autopilot"), "o/Sample", true, ["Sample.Api"], "1.0.52", "1.0.78", 0);

		var entry = ledger.Query(kind: "publish").Single();
		entry.Actor.Should().Be("autopilot");
		entry.Data.GetProperty("nextVersion").GetString().Should().Be("1.0.78");
		entry.Data.GetProperty("failuresAtTheTime").GetInt32().Should().Be(0);
		entry.Data.GetProperty("packageIds")[0].GetString().Should().Be("Sample.Api");
	}

	[Fact]
	public async Task CaptureDiff_ShowsTrackedChanges_AndTheContentOfNewFiles()
	{
		var (_, recorder, _) = Open();
		var clone = await InitRepoAsync();
		await File.WriteAllTextAsync(Path.Combine(clone, "tracked.txt"), "two\n", TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(Path.Combine(clone, "new.cs"), "class New {}\n", TestContext.Current.CancellationToken);

		var diff = await recorder.CaptureDiffAsync(clone, ["tracked.txt", "new.cs"], TestContext.Current.CancellationToken);

		diff.Should().Contain("-one").And.Contain("+two");
		diff.Should().Contain("=== new file: new.cs").And.Contain("class New {}");
	}

	[Fact]
	public async Task CaptureDiff_WithNoFiles_IsEmpty()
	{
		var (_, recorder, _) = Open();

		(await recorder.CaptureDiffAsync(await InitRepoAsync(), [], TestContext.Current.CancellationToken)).Should().BeEmpty();
	}

	[Fact]
	public async Task RecordPush_OfARealCommit_RecordsTheShaTheFilesAndAPacket()
	{
		var (ledger, recorder, _) = Open();
		var clone = await InitRepoAsync();
		await File.WriteAllTextAsync(Path.Combine(clone, "added.txt"), "x\n", TestContext.Current.CancellationToken);
		await LocalRepoService.RunCommandAsync(clone, "git", ["add", "-A"], TestContext.Current.CancellationToken);
		await LocalRepoService.RunCommandAsync(clone, "git", ["commit", "-q", "-m", "second"], TestContext.Current.CancellationToken);

		await recorder.RecordPushAsync(Item("autopilot"), "o/Sample", true, null, clone, TestContext.Current.CancellationToken);

		var entry = ledger.Query(kind: "push").Single();
		entry.Data.GetProperty("succeeded").GetBoolean().Should().BeTrue();
		entry.Data.GetProperty("sha").GetString().Should().HaveLength(40);
		entry.Data.GetProperty("branch").GetString().Should().Be("main");
		entry.Data.GetProperty("files")[0].GetString().Should().Be("added.txt");
		entry.Data.GetProperty("packet").GetProperty("relativePath").GetString().Should().EndWith(".json.gz");
	}

	[Fact]
	public async Task RecordPush_OfARefusal_RecordsWhyAndNoCommit()
	{
		var (ledger, recorder, _) = Open();

		await recorder.RecordPushAsync(Item(), "o/Sample", false, "the clone is behind origin", null, TestContext.Current.CancellationToken);

		var entry = ledger.Query(kind: "push").Single();
		entry.Data.GetProperty("succeeded").GetBoolean().Should().BeFalse();
		entry.Data.GetProperty("refusalReason").GetString().Should().Be("the clone is behind origin");
		entry.Data.TryGetProperty("sha", out var sha).Should().BeTrue();
		sha.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
	}
}
