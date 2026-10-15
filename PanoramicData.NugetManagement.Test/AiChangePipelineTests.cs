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

	private AiChangePipeline Pipeline(Func<string, bool>? isAllowed = null, int maxPaths = 3)
		=> new(_git, _tests, _work, _github, new AiChangeOptions
		{
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

	[Fact]
	public async Task Run_ADisallowedPath_FailsTheLocalGate()
	{
		RedThenGreen();
		_git.Changed = [new ChangedFile("secrets/.env", new HashSet<int> { 1 })];

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
			new ChangedFile("Lib/A.cs", new HashSet<int> { 10 }),
			new ChangedFile("Lib/B.cs", new HashSet<int> { 10 }),
			new ChangedFile("Lib.Test/ATests.cs", new HashSet<int> { 1 }),
			new ChangedFile("Lib.Test/BTests.cs", new HashSet<int> { 1 })
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
