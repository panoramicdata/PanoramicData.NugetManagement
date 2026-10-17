using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="AiChangePipeline"/>: every gate, and that the clone is always put back.
/// </summary>
public class AiChangePipelineTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private readonly FakeAiChangeGit _git = new();
	private readonly FakeAiChangeTests _tests = new();
	private readonly FakeAiChangeWork _work = new();
	private readonly FakeGitHubWriteApi _github = new();
	private readonly List<string> _said = [];
	private readonly List<TimeSpan> _waits = [];

	private static AiChangeRequest Issue() => new("o", "r", AiChangeKind.IssueFix, "issue-42-a1b2", "Fix #42", 42, true);

	private static AiChangeRequest Rule() => new("o", "r", AiChangeKind.RuleFix, "rule-ci-15-a1b2", "Fix CI-15", null, false);

	private AiChangePipeline Pipeline(
		Func<string, bool>? isAllowed = null,
		int maxPaths = 3,
		Func<CancellationToken, Task>? changesReady = null)
		=> new(_git, _tests, _work, _github, new AiChangeOptions
		{
			ChangesReady = changesReady,
			Limits = new AiChangeLimits(
				isAllowed ?? (_ => true),
				path => path.Contains(".Test", StringComparison.Ordinal),
				maxPaths),
			Delay = (span, _) =>
			{
				_waits.Add(span);
				return Task.CompletedTask;
			}
		});

	private Task<AiChangeOutcome> RunAsync(AiChangeRequest request, CancellationToken? token = null)
		=> Pipeline().RunAsync(request, _said.Add, token ?? TestContext.Current.CancellationToken);

	private void RedThenGreen()
	{
		_tests.OnlyRuns.Enqueue(new TestRun(false, "Expected 3 but found 2.\n  total: 1\n  failed: 1"));
		_tests.OnlyRuns.Enqueue(new TestRun(true, "total: 1\n  failed: 0"));
	}

	[Fact]
	public async Task Run_AnIssueWithRedThenGreenAndGreenCi_IsMergedAtTheGatedSha()
	{
		RedThenGreen();

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.Merged);
		outcome.PullRequestNumber.Should().Be(7);
		_github.MergedSha.Should().Be("abc123", "the merge is conditional on the commit that passed the gates");
		_git.DeletedRemote.Should().BeTrue("a merged branch is tidied away");
	}

	[Fact]
	public async Task Run_ARefusedCloneNeverBranches()
	{
		_git.Refusal = "has uncommitted changes";

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.Refused);
		_git.Calls.Should().NotContain(call => call.StartsWith("branch:", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Run_NoValidRedTestInThreeTries_CommentsOnTheIssueAndStops()
	{
		_tests.OnlyRuns.Enqueue(new TestRun(true, "total: 1"));
		_tests.OnlyRuns.Enqueue(new TestRun(true, "total: 1"));
		_tests.OnlyRuns.Enqueue(new TestRun(true, "total: 1"));

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.NoValidRedTest);
		_work.RedAttempts.Should().Be(3);
		_work.Feedback[1].Should().Contain("passes", "the second try is told why the first was rejected");
		_github.Comments.Should().ContainSingle().Which.Should().Contain("test");
		_git.Calls.Should().NotContain("push");
		_git.Calls.Should().Contain("return");
	}

	[Fact]
	public async Task Run_ARedTestThatFailsForTheWrongReason_IsRejectedAndTheModelIsToldWhy()
	{
		_tests.OnlyRuns.Enqueue(new TestRun(false, "Parser.Tests.cs(4,9): error CS0103: The name 'Foo' does not exist\nBuild FAILED."));
		_tests.OnlyRuns.Enqueue(new TestRun(false, "Expected 3 but found 2.\n  total: 1\n  failed: 1"));
		_tests.OnlyRuns.Enqueue(new TestRun(true, "total: 1\n  failed: 0"));

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.Merged);
		_work.RedAttempts.Should().Be(2, "the first attempt was rejected and the second was accepted");
		_work.Feedback[1].Should().Contain("compile", "the second attempt is told that the first did not compile");
	}

	[Fact]
	public async Task Run_ARuleFixSkipsTheRedStep()
	{
		var outcome = await RunAsync(Rule());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.Merged);
		_work.RedAttempts.Should().Be(0);
	}

	[Fact]
	public async Task Run_AFixThatFails_PushesNothingAndReturnsToMain()
	{
		RedThenGreen();
		_work.FixSucceeds = false;

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.LocalGateFailed);
		_git.Calls.Should().NotContain("push");
		_git.Calls.Should().Contain("return");
	}

	[Fact]
	public async Task Run_ARegressionInTheSuite_FailsTheLocalGate()
	{
		RedThenGreen();
		_tests.All = new TestRun(false, "total: 10\n  failed: 1");

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.LocalGateFailed);
		outcome.Message.Should().Contain("tests");
	}

	[Fact]
	public async Task Run_UncoveredChangedCode_FailsTheLocalGate()
	{
		RedThenGreen();
		_tests.Cobertura = """
			<coverage><packages><package><classes>
			  <class filename="C:\r\Lib\Parser.cs"><lines><line number="10" hits="0" /></lines></class>
			</classes></package></packages></coverage>
			""";

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.LocalGateFailed);
		outcome.Message.Should().Contain("covered");
	}

	private static string CoberturaFor(params (string File, int From, int To, int Hits)[] spans)
		=> "<coverage><packages><package><classes>"
			+ string.Concat(spans.Select(span =>
				$"<class filename=\"C:\\r\\{span.File}\"><lines>"
				+ string.Concat(Enumerable.Range(span.From, span.To - span.From + 1)
					.Select(line => $"<line number=\"{line}\" hits=\"{span.Hits}\" />"))
				+ "</lines></class>"))
			+ "</classes></package></packages></coverage>";

	[Fact]
	public async Task Run_ATestFileThatRunsCannotCoverForProductionCodeThatDoesNot()
	{
		RedThenGreen();
		_git.Changed =
		[
			new ChangedFileLines("Lib/Parser.cs", Enumerable.Range(10, 10).ToHashSet()),
			new ChangedFileLines("Lib.Test/ParserTests.cs", Enumerable.Range(1, 40).ToHashSet())
		];
		_tests.Cobertura = CoberturaFor(("Lib\\Parser.cs", 10, 19, 0), ("Lib.Test\\ParserTests.cs", 1, 40, 1));

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.LocalGateFailed,
			"forty executed test lines would otherwise carry ten untested production lines to 80%, and the "
				+ "gate is meant to say the changed code was exercised, not that the test was");
		outcome.Message.Should().Contain("covered");
	}

	[Fact]
	public async Task Run_ADisallowedPath_FailsTheLocalGate()
	{
		RedThenGreen();
		_git.Changed = [new ChangedFileLines("secrets/.env", new HashSet<int> { 1 })];

		var outcome = await Pipeline(isAllowed: path => !path.StartsWith("secrets/", StringComparison.Ordinal))
			.RunAsync(Issue(), _said.Add, TestContext.Current.CancellationToken);

		outcome.Kind.Should().Be(AiChangeOutcomeKind.LocalGateFailed);
		outcome.Message.Should().Contain("secrets/.env");
	}

	[Fact]
	public async Task Run_TestFilesDoNotCountAgainstThePathCap()
	{
		RedThenGreen();
		_git.Changed =
		[
			new ChangedFileLines("Lib/A.cs", new HashSet<int> { 10 }),
			new ChangedFileLines("Lib/B.cs", new HashSet<int> { 10 }),
			new ChangedFileLines("Lib.Test/ATests.cs", new HashSet<int> { 1 }),
			new ChangedFileLines("Lib.Test/BTests.cs", new HashSet<int> { 1 })
		];
		_tests.Cobertura = """
			<coverage><packages><package><classes>
			  <class filename="C:\r\Lib\A.cs"><lines><line number="10" hits="1" /></lines></class>
			  <class filename="C:\r\Lib\B.cs"><lines><line number="10" hits="1" /></lines></class>
			  <class filename="C:\r\Lib.Test\ATests.cs"><lines><line number="1" hits="1" /></lines></class>
			  <class filename="C:\r\Lib.Test\BTests.cs"><lines><line number="1" hits="1" /></lines></class>
			</classes></package></packages></coverage>
			""";

		var outcome = await Pipeline(maxPaths: 2).RunAsync(Issue(), _said.Add, TestContext.Current.CancellationToken);

		outcome.Kind.Should().Be(AiChangeOutcomeKind.Merged,
			"two production files and two test files is within a cap of two, because the tests that prove a fix are not part of its footprint");
	}

	[Fact]
	public async Task Run_AFailedCheck_LeavesThePullRequestOpenAndKeepsTheRemoteBranch()
	{
		RedThenGreen();
		_github.Checks.Enqueue([new CheckRunState("build", CheckRunConclusion.Failed)]);

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.LeftOpenCiFailed);
		outcome.Message.Should().Contain("build");
		_github.Calls.Should().NotContain("merge");
		_git.DeletedRemote.Should().BeFalse("the pull request still needs its branch");
		_git.Calls.Should().Contain("return", "the clone is still put back on main");
	}

	[Fact]
	public async Task Run_ChecksThatNeverFinish_TimeOutAndLeaveThePullRequestOpen()
	{
		RedThenGreen();
		_github.WhenQueueEmpty = [new CheckRunState("ci", CheckRunConclusion.Pending)];

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.LeftOpenCiTimedOut);
		_github.Calls.Should().NotContain("merge");
		_waits.Sum(span => span.TotalMinutes).Should().BeGreaterThanOrEqualTo(30);
	}

	[Fact]
	public async Task Run_NoChecksRightAfterThePush_IsNotTakenAsNoCi()
	{
		RedThenGreen();
		_github.Checks.Enqueue([]);
		_github.Checks.Enqueue([new CheckRunState("ci", CheckRunConclusion.Failed)]);

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.LeftOpenCiFailed,
			"an empty list straight after a push is GitHub not having registered the runs yet, and merging then would skip CI that was about to start");
	}

	[Fact]
	public async Task Run_NoChecksOnceTheGraceHasPassed_MergesOnTheLocalGate()
	{
		RedThenGreen();
		_github.WhenQueueEmpty = [];

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.Merged);
		_waits.Sum(span => span.TotalMinutes).Should().BeGreaterThanOrEqualTo(2, "the grace period is waited out before believing there is no CI");
	}

	[Fact]
	public async Task Run_AMergeGitHubRefuses_LeavesThePullRequestOpen()
	{
		RedThenGreen();
		_github.MergeThrows = true;

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.LeftOpenMergeRefused);
		outcome.Message.Should().Contain("Head branch was modified");
		_git.DeletedRemote.Should().BeFalse();
	}

	private Task<AiChangeOutcome> DeliverAsync(AiChangeRequest request, params string[] ownedPaths)
		=> Pipeline().DeliverAsync(request, ownedPaths.ToHashSet(), _said.Add, TestContext.Current.CancellationToken);

	[Fact]
	public async Task Deliver_WorkAlreadyInTheTree_IsGatedPushedAndMergedWithoutRunningTheModel()
	{
		var outcome = await DeliverAsync(Rule(), "Lib/Parser.cs");

		outcome.Kind.Should().Be(AiChangeOutcomeKind.Merged);
		_work.FixCalls.Should().Be(0, "the edits were made before delivery, so there is nothing for the model to do");
		_work.RedAttempts.Should().Be(0);
		_git.OwnedPathsSeen.Should().BeEquivalentTo(new[] { "Lib/Parser.cs" });
		_github.MergedSha.Should().Be("abc123");
	}

	[Fact]
	public async Task Deliver_WhenTheTreeHoldsChangesTheSessionDidNotMake_LeavesEverythingAlone()
	{
		_git.DeliverRefusal = "the clone has uncommitted changes the session did not make: notes.txt";

		var outcome = await DeliverAsync(Rule(), "Lib/Parser.cs");

		outcome.Kind.Should().Be(AiChangeOutcomeKind.Refused);
		_git.Calls.Should().NotContain(call => call.StartsWith("branch:", StringComparison.Ordinal));
		_git.Calls.Should().NotContain("return",
			"returning to main resets the tree, and that would destroy somebody else's uncommitted work");
		_github.Calls.Should().BeEmpty();
	}

	[Fact]
	public async Task Deliver_AFailedLocalGate_ReturnsTheCloneToMainAndPushesNothing()
	{
		_tests.All = new TestRun(false, "total: 10\n  failed: 1");

		var outcome = await DeliverAsync(Rule(), "Lib/Parser.cs");

		outcome.Kind.Should().Be(AiChangeOutcomeKind.LocalGateFailed);
		_git.Calls.Should().NotContain("push");
		_git.Calls.Should().Contain("return");
	}

	[Fact]
	public async Task Run_AReturnToMainThatFails_IsSaidInTheConsoleEvenAfterAMerge()
	{
		RedThenGreen();
		_git.ReturnProblem = "could not return to main: checkout failed, a file is locked";

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.Merged);
		_said.Should().Contain(line => line.Contains("could not return to main", StringComparison.Ordinal),
			"a clone left on an ai/ branch blocks every later fix, and the console must say so rather than report only the merge");
	}

	[Fact]
	public async Task Run_APushThatFails_IsAnOutcomeNotARawExceptionAndCleansUp()
	{
		RedThenGreen();
		_git.PushThrows = true;

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.DeliveryFailed);
		outcome.Message.Should().Contain("remote rejected");
		_git.Calls.Should().Contain("return");
		_github.Calls.Should().NotContain(call => call.StartsWith("pr:", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Run_APullRequestThatCannotBeOpened_DeletesTheBranchItJustPushed()
	{
		RedThenGreen();
		_github.CreatePullRequestThrows = true;

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.DeliveryFailed);
		_git.DeletedRemote.Should().BeTrue(
			"the branch is on origin with no pull request to own it, and nothing else will ever clean it up");
	}

	private Func<CancellationToken, Task> Announce => _ =>
	{
		_git.Calls.Add("changes-ready");
		return Task.CompletedTask;
	};

	[Fact]
	public async Task Run_TellsTheCallerTheChangesAreReady_BeforeCommittingAndBeforeTheCloneIsReset()
	{
		RedThenGreen();

		await Pipeline(changesReady: Announce).RunAsync(Issue(), _said.Add, TestContext.Current.CancellationToken);

		var calls = _git.Calls;
		calls.Count(call => call == "changes-ready").Should().Be(1);
		calls.IndexOf("changes-ready").Should().BeLessThan(calls.IndexOf("commit"));
		calls.IndexOf("changes-ready").Should().BeLessThan(calls.IndexOf("return"),
			"after the reset the clone holds nothing, which is the whole reason the caller wants to keep a copy");
	}

	[Fact]
	public async Task Deliver_TellsTheCallerTheChangesAreReadyToo()
	{
		await Pipeline(changesReady: Announce)
			.DeliverAsync(Rule(), new HashSet<string> { "Lib/Parser.cs" }, _said.Add, TestContext.Current.CancellationToken);

		_git.Calls.Should().Contain("changes-ready");
		_git.Calls.IndexOf("changes-ready").Should().BeLessThan(_git.Calls.IndexOf("return"));
	}

	[Fact]
	public async Task Run_DoesNotAnnounceChangesWhenNoFixWasMade()
	{
		RedThenGreen();
		_work.FixSucceeds = false;

		await Pipeline(changesReady: Announce).RunAsync(Issue(), _said.Add, TestContext.Current.CancellationToken);

		_git.Calls.Should().NotContain("changes-ready", "a failed fix has nothing worth keeping a copy of");
	}

	[Fact]
	public async Task Run_StillDeliversWhenTheCallerFailsToKeepItsCopy()
	{
		RedThenGreen();

		var outcome = await Pipeline(changesReady: _ => throw new IOException("disk full"))
			.RunAsync(Issue(), _said.Add, TestContext.Current.CancellationToken);

		outcome.Kind.Should().Be(AiChangeOutcomeKind.Merged,
			"keeping a copy for the Changes tab is a convenience and must never stop a change that passed its gates");
	}

	[Fact]
	public async Task Run_StoppedPartWay_StillReturnsTheCloneToMain()
	{
		RedThenGreen();
		using var cancelled = new CancellationTokenSource();
		await cancelled.CancelAsync();

		var act = () => RunAsync(Issue(), cancelled.Token);

		await act.Should().ThrowAsync<OperationCanceledException>();
		_git.Calls.Should().Contain("return", "a stop must not leave a branch or dirty files behind");
	}
}
