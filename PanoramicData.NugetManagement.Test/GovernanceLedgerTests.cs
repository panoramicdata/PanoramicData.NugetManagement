using PanoramicData.NugetManagement.Web.Services.Governance;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="GovernanceLedger"/>: the record Autopilot's actions are written to, which is only
/// worth anything if a change to it can be seen.
/// </summary>
public class GovernanceLedgerTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _folder = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		$"ledger-{Guid.NewGuid():n}");

	public void Dispose()
	{
		if (Directory.Exists(_folder))
		{
			Directory.Delete(_folder, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private sealed class FixedClock(DateTimeOffset now) : TimeProvider
	{
		public DateTimeOffset Now { get; set; } = now;

		public override DateTimeOffset GetUtcNow() => Now;
	}

	private GovernanceLedger NewLedger(FixedClock? clock = null, string? policyHash = null)
		=> new(_folder, () => policyHash, clock);

	private string OnlyFile() => Directory.EnumerateFiles(_folder, "ledger-*.jsonl").Single();

	[Fact]
	public void Append_NumbersEntriesFromOne_AndChainsEachToThePrevious()
	{
		var ledger = NewLedger();

		var first = ledger.Append("autopilot", "cycle_started", "o/r", "TST-10", new { note = "a" });
		var second = ledger.Append("autopilot", "cycle_ended", "o/r");

		first.Seq.Should().Be(1);
		first.PrevHash.Should().Be(GovernanceLedger.Genesis);
		second.Seq.Should().Be(2);
		second.PrevHash.Should().Be(first.Hash);
		ledger.Head.Should().Be(second.Hash);
	}

	[Fact]
	public void Verify_OnAnUntouchedLedger_IsIntact()
	{
		var ledger = NewLedger();
		ledger.Append("autopilot", "push", "o/r", data: new { sha = "abc" });
		ledger.Append("claude-review", "review_verdict", "o/r", data: new { suitability = "fit" });
		ledger.Append("user", "policy_changed");

		var result = ledger.Verify();

		result.Intact.Should().BeTrue();
		result.Entries.Should().Be(3);
		result.HeadHash.Should().Be(ledger.Head);
		result.StartsAfterRetention.Should().BeFalse();
	}

	[Fact]
	public void Verify_OnAnEmptyLedger_IsIntact()
		=> NewLedger().Verify().Should().BeEquivalentTo(new { Intact = true, Entries = 0L });

	[Fact]
	public void Verify_DetectsAnEditedEntry_AndNamesIt()
	{
		var ledger = NewLedger();
		ledger.Append("autopilot", "push", "o/r", data: new { sha = "abc123" });
		ledger.Append("autopilot", "push", "o/r", data: new { sha = "def456" });
		ledger.Append("autopilot", "push", "o/r", data: new { sha = "789abc" });

		File.WriteAllText(OnlyFile(), File.ReadAllText(OnlyFile()).Replace("def456", "000000", StringComparison.Ordinal));

		var result = ledger.Verify();

		result.Intact.Should().BeFalse();
		result.FirstBrokenSeq.Should().Be(2);
		result.Problem.Should().Contain("no longer matches its hash");
	}

	[Fact]
	public void Verify_DetectsADeletedEntry()
	{
		var ledger = NewLedger();
		ledger.Append("autopilot", "a");
		ledger.Append("autopilot", "b");
		ledger.Append("autopilot", "c");

		var lines = File.ReadAllLines(OnlyFile());
		File.WriteAllLines(OnlyFile(), [lines[0], lines[2]]);

		var result = ledger.Verify();

		result.Intact.Should().BeFalse();
		result.FirstBrokenSeq.Should().Be(3);
		result.Problem.Should().Contain("missing or out of order");
	}

	[Fact]
	public void Verify_DetectsReorderedEntries()
	{
		var ledger = NewLedger();
		ledger.Append("autopilot", "a");
		ledger.Append("autopilot", "b");
		ledger.Append("autopilot", "c");

		var lines = File.ReadAllLines(OnlyFile());
		File.WriteAllLines(OnlyFile(), [lines[0], lines[2], lines[1]]);

		ledger.Verify().Intact.Should().BeFalse();
	}

	[Fact]
	public void Verify_DetectsALineWithItsHashRemoved()
	{
		var ledger = NewLedger();
		ledger.Append("autopilot", "a");

		var line = File.ReadAllText(OnlyFile()).TrimEnd();
		File.WriteAllText(OnlyFile(), line[..line.IndexOf(",\"hash\"", StringComparison.Ordinal)] + "}\n");

		ledger.Verify().Problem.Should().Contain("no hash");
	}

	[Fact]
	public void Verify_DetectsATailThatWasTruncated_OnlyWhenSomethingFollowsIt()
	{
		// Truncating the last entry cannot be seen from inside the chain: nothing after it names it. That is
		// why the head hash is returned, to be compared with one recorded elsewhere.
		var ledger = NewLedger();
		ledger.Append("autopilot", "a");
		var head = ledger.Append("autopilot", "b").Hash;

		var lines = File.ReadAllLines(OnlyFile());
		File.WriteAllLines(OnlyFile(), [lines[0]]);

		var result = ledger.Verify();

		result.Intact.Should().BeTrue();
		result.HeadHash.Should().NotBe(head, "the caller can tell the tail went by comparing heads");
	}

	[Fact]
	public void ANewLedgerOverTheSameFolder_ExtendsTheChain()
	{
		var first = NewLedger();
		first.Append("autopilot", "a");
		var before = first.Append("autopilot", "b");

		var reopened = NewLedger();
		var next = reopened.Append("autopilot", "c");

		next.Seq.Should().Be(3);
		next.PrevHash.Should().Be(before.Hash);
		reopened.Verify().Intact.Should().BeTrue();
	}

	[Fact]
	public void EntriesCarryThePolicyHashInForceWhenTheyWereWritten()
	{
		var ledger = NewLedger(policyHash: "abc123");

		ledger.Append("autopilot", "push").PolicyHash.Should().Be("abc123");
		ledger.Query().Single().PolicyHash.Should().Be("abc123");
	}

	[Fact]
	public void Append_WritesOneFilePerMonth_AndTheChainSpansThem()
	{
		var clock = new FixedClock(new DateTimeOffset(2026, 9, 30, 23, 0, 0, TimeSpan.Zero));
		var ledger = NewLedger(clock);
		ledger.Append("autopilot", "a");

		clock.Now = new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero);
		ledger.Append("autopilot", "b");

		Directory.EnumerateFiles(_folder).Select(Path.GetFileName).Order().Should()
			.Equal("ledger-2026-09.jsonl", "ledger-2026-10.jsonl");
		ledger.Verify().Intact.Should().BeTrue();
	}

	[Fact]
	public void Query_ReturnsNewestFirst_AndFilters()
	{
		var ledger = NewLedger();
		ledger.Append("autopilot", "push", "o/a");
		ledger.Append("autopilot", "publish", "o/b");
		ledger.Append("autopilot", "push", "o/b");

		ledger.Query().Select(e => e.Seq).Should().Equal(3, 2, 1);
		ledger.Query(kind: "push").Select(e => e.Seq).Should().Equal(3, 1);
		ledger.Query(repository: "o/b").Select(e => e.Seq).Should().Equal(3, 2);
		ledger.Query(take: 1).Single().Seq.Should().Be(3);
		ledger.Query(beforeSeq: 3).Select(e => e.Seq).Should().Equal(2, 1);
	}

	[Fact]
	public void Data_RoundTripsWhateverWasWritten()
	{
		var ledger = NewLedger();
		ledger.Append("autopilot", "ai_session", data: new { Model = "qwen3.8:27b", Turns = 12, Files = new[] { "a.cs", "b.cs" } });

		var data = ledger.Query().Single().Data;

		data.GetProperty("model").GetString().Should().Be("qwen3.8:27b");
		data.GetProperty("turns").GetInt32().Should().Be(12);
		data.GetProperty("files").GetArrayLength().Should().Be(2);
	}

	[Fact]
	public void Append_FromManyThreads_KeepsTheChainWhole()
	{
		var ledger = NewLedger();

		Parallel.For(0, 200, i => ledger.Append("autopilot", "tick", data: new { i }));

		var result = ledger.Verify();

		result.Intact.Should().BeTrue();
		result.Entries.Should().Be(200);
	}

	[Fact]
	public void ApplyRetention_RemovesWholeOldMonths_RecordsIt_AndTheChainStillVerifies()
	{
		var clock = new FixedClock(new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero));
		var ledger = NewLedger(clock);
		ledger.Append("autopilot", "old");

		clock.Now = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
		ledger.Append("autopilot", "recent");

		var removed = ledger.ApplyRetention(months: 12);

		removed.Should().Equal("ledger-2025-01.jsonl");
		Directory.EnumerateFiles(_folder).Select(Path.GetFileName).Should().NotContain("ledger-2025-01.jsonl");
		ledger.Query(kind: "retention_applied").Should().ContainSingle();

		var result = ledger.Verify();
		result.Intact.Should().BeTrue();
		result.StartsAfterRetention.Should().BeTrue();
	}

	[Fact]
	public void ApplyRetention_NeverRemovesTheCurrentMonth()
	{
		var clock = new FixedClock(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero));
		var ledger = NewLedger(clock);
		ledger.Append("autopilot", "a");

		ledger.ApplyRetention(months: 1).Should().BeEmpty();
		ledger.Query().Should().ContainSingle();
	}
}
