using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Services;
using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

internal sealed class FakeAiChangeGit : IAiChangeGit
{
	public string? Refusal { get; set; }
	public IReadOnlyList<ChangedFile> Changed { get; set; } = [new ChangedFile("Lib/Parser.cs", new HashSet<int> { 10 })];
	public List<string> Calls { get; } = [];
	public bool? DeletedRemote { get; private set; }

	public Task<string?> RefusalAsync(CancellationToken cancellationToken)
	{
		Calls.Add("refusal");
		return Task.FromResult(Refusal);
	}

	public string? DeliverRefusal { get; set; }
	public IReadOnlySet<string>? OwnedPathsSeen { get; private set; }

	public Task<string?> DeliverRefusalAsync(IReadOnlySet<string> ownedPaths, CancellationToken cancellationToken)
	{
		Calls.Add("deliver-refusal");
		OwnedPathsSeen = ownedPaths;
		return Task.FromResult(DeliverRefusal);
	}

	public Task CreateBranchAsync(string branch, CancellationToken cancellationToken)
	{
		Calls.Add($"branch:{branch}");
		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<ChangedFile>> ChangedFilesAsync(CancellationToken cancellationToken)
		=> Task.FromResult(Changed);

	public Task<string> CommitAsync(string message, CancellationToken cancellationToken)
	{
		Calls.Add("commit");
		return Task.FromResult("abc123");
	}

	public Task PushBranchAsync(string branch, CancellationToken cancellationToken)
	{
		Calls.Add("push");
		return Task.CompletedTask;
	}

	public Task ReturnToMainAsync(string branch, bool deleteRemoteBranch, CancellationToken cancellationToken)
	{
		Calls.Add("return");
		DeletedRemote = deleteRemoteBranch;
		return Task.CompletedTask;
	}
}

internal sealed class FakeAiChangeTests : IAiChangeTests
{
	public IssueFixCheckStep Build { get; set; } = new(true, string.Empty);
	public TestRun All { get; set; } = new(true, "total: 10\n  failed: 0");
	public Queue<TestRun> OnlyRuns { get; } = new();
	public string? Cobertura { get; set; } = """
		<coverage><packages><package><classes>
		  <class filename="C:\r\Lib\Parser.cs"><lines><line number="10" hits="3" /></lines></class>
		</classes></package></packages></coverage>
		""";

	public Task<IssueFixCheckStep> BuildAsync(CancellationToken cancellationToken) => Task.FromResult(Build);

	public Task<TestRun> RunAllAsync(CancellationToken cancellationToken) => Task.FromResult(All);

	public Task<TestRun> RunOnlyAsync(string testFilter, CancellationToken cancellationToken)
		=> Task.FromResult(OnlyRuns.Count > 0 ? OnlyRuns.Dequeue() : new TestRun(true, "total: 1"));

	public Task<string?> CoberturaAsync(CancellationToken cancellationToken) => Task.FromResult(Cobertura);
}

internal sealed class FakeAiChangeWork : IAiChangeWork
{
	public int RedAttempts { get; private set; }
	public List<string?> Feedback { get; } = [];
	public string? RedFilter { get; set; } = "FullyQualifiedName~ParserTests";
	public bool FixSucceeds { get; set; } = true;

	public Task<string?> WriteRedTestAsync(AiChangeRequest request, string? feedback, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		RedAttempts++;
		Feedback.Add(feedback);
		return Task.FromResult(RedFilter);
	}

	public int FixCalls { get; private set; }

	public Task<AiFixOutcome> FixAsync(AiChangeRequest request, string? redTestFilter, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		FixCalls++;
		return Task.FromResult(new AiFixOutcome(FixSucceeds, 1, "done"));
	}
}

internal sealed class FakeGitHubWriteApi : IGitHubWriteApi
{
	public Queue<IReadOnlyList<CheckRunState>> Checks { get; } = new();
	public IReadOnlyList<CheckRunState> WhenQueueEmpty { get; set; } = [new CheckRunState("ci", CheckRunConclusion.Passed)];
	public List<string> Calls { get; } = [];
	public List<string> Comments { get; } = [];
	public string? MergedSha { get; private set; }
	public bool MergeThrows { get; set; }

	public Task<int> CreatePullRequestAsync(string owner, string name, string head, string baseBranch, string title, string body, CancellationToken cancellationToken)
	{
		Calls.Add($"pr:{head}->{baseBranch}");
		return Task.FromResult(7);
	}

	public Task<IReadOnlyList<CheckRunState>> GetCheckRunsAsync(string owner, string name, string sha, CancellationToken cancellationToken)
	{
		Calls.Add("checks");
		return Task.FromResult(Checks.Count > 0 ? Checks.Dequeue() : WhenQueueEmpty);
	}

	public Task MergePullRequestAsync(string owner, string name, int number, string expectedHeadSha, CancellationToken cancellationToken)
	{
		Calls.Add("merge");

		if (MergeThrows)
		{
			throw new InvalidOperationException("Head branch was modified");
		}

		MergedSha = expectedHeadSha;
		return Task.CompletedTask;
	}

	public Task CommentAsync(string owner, string name, int number, string body, CancellationToken cancellationToken)
	{
		Comments.Add(body);
		return Task.CompletedTask;
	}

	public Task<int> CreateIssueAsync(string owner, string name, string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken)
		=> throw new NotSupportedException();

	public Task UpdateIssueBodyAsync(string owner, string name, int number, string body, CancellationToken cancellationToken)
		=> throw new NotSupportedException();

	public Task CloseIssueAsync(string owner, string name, int number, CancellationToken cancellationToken)
		=> throw new NotSupportedException();

	public Task ClosePullRequestAsync(string owner, string name, int number, CancellationToken cancellationToken)
		=> throw new NotSupportedException();
}
