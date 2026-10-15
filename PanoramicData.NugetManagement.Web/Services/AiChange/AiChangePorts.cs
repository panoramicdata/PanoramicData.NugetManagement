namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>What kind of AI task is asking for a change.</summary>
public enum AiChangeKind
{
	/// <summary>A governance rule that has no deterministic remediation.</summary>
	RuleFix,

	/// <summary>A human-raised issue.</summary>
	IssueFix
}

/// <summary>
/// One change the AI is to make and deliver.
/// </summary>
/// <param name="Owner">The repository owner.</param>
/// <param name="Name">The repository name.</param>
/// <param name="Kind">What is asking.</param>
/// <param name="Slug">A unique, branch-safe label such as <c>issue-42-a1b2</c>; the branch is <c>ai/&lt;slug&gt;</c>.</param>
/// <param name="Title">The pull request title.</param>
/// <param name="IssueNumber">The issue, when there is one; it is closed by the pull request.</param>
/// <param name="RequiresRedTest">True for a reported bug, which must be reproduced by a failing test first.</param>
public sealed record AiChangeRequest(
	string Owner,
	string Name,
	AiChangeKind Kind,
	string Slug,
	string Title,
	int? IssueNumber,
	bool RequiresRedTest);

/// <summary>How an attempt ended.</summary>
public enum AiChangeOutcomeKind
{
	/// <summary>The pull request was merged.</summary>
	Merged,

	/// <summary>No test could be made to reproduce the report; the issue was told.</summary>
	NoValidRedTest,

	/// <summary>A local gate failed; nothing was pushed.</summary>
	LocalGateFailed,

	/// <summary>A CI check failed; the pull request is left open.</summary>
	LeftOpenCiFailed,

	/// <summary>CI did not finish in time; the pull request is left open.</summary>
	LeftOpenCiTimedOut,

	/// <summary>GitHub refused the merge (for instance the head moved); the pull request is left open.</summary>
	LeftOpenMergeRefused,

	/// <summary>The clone was not in a state to start from.</summary>
	Refused
}

/// <summary>
/// The result of an attempt.
/// </summary>
/// <param name="Kind">How it ended.</param>
/// <param name="Message">One line saying why, for the console.</param>
/// <param name="PullRequestNumber">The pull request, when one was opened.</param>
public sealed record AiChangeOutcome(AiChangeOutcomeKind Kind, string Message, int? PullRequestNumber = null);

/// <summary>
/// What the AI may touch.
/// </summary>
/// <param name="IsAllowedPath">Whether a repository-relative path may be written.</param>
/// <param name="IsTestPath">Whether a path is a test file; these do not count against the cap.</param>
/// <param name="MaxPaths">The most non-test paths one change may touch.</param>
public sealed record AiChangeLimits(Func<string, bool> IsAllowedPath, Func<string, bool> IsTestPath, int MaxPaths);

/// <summary>
/// The pipeline's tunables.
/// </summary>
public sealed class AiChangeOptions
{
	/// <summary>What the AI may touch.</summary>
	public required AiChangeLimits Limits { get; init; }

	/// <summary>How many tries the model gets at a valid red test.</summary>
	public int MaxRedAttempts { get; init; } = 3;

	/// <summary>How long to wait for CI before leaving the pull request open.</summary>
	public TimeSpan CiTimeout { get; init; } = TimeSpan.FromMinutes(30);

	/// <summary>How often to ask GitHub for the checks.</summary>
	public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

	/// <summary>
	/// How long an empty list of checks is believed to be transient. GitHub registers check runs a moment
	/// after a push, so "no checks" straight away does not mean "no CI".
	/// </summary>
	public TimeSpan NoChecksGrace { get; init; } = TimeSpan.FromMinutes(2);

	/// <summary>Waits between polls; replaced in tests so they do not sleep.</summary>
	public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
}

/// <summary>The git operations the pipeline needs, on one clone.</summary>
public interface IAiChangeGit
{
	/// <summary>
	/// Fast-forwards <c>main</c> and confirms the clone is clean and on it.
	/// </summary>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>Null when ready; otherwise why the clone cannot be started from.</returns>
	Task<string?> RefusalAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Confirms the clone is on <c>main</c> and that every uncommitted change in it is one the caller made.
	/// </summary>
	/// <param name="ownedPaths">The repository-relative paths the caller wrote.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>Null when the changes are all the caller's; otherwise why they cannot be delivered.</returns>
	/// <remarks>
	/// Returning to <c>main</c> afterwards resets the tree, so a change nobody here made must stop the
	/// delivery before it begins, not be swept away by it.
	/// </remarks>
	Task<string?> DeliverRefusalAsync(IReadOnlySet<string> ownedPaths, CancellationToken cancellationToken);

	/// <summary>Creates and checks out a branch from <c>main</c>.</summary>
	/// <param name="branch">The branch name.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	Task CreateBranchAsync(string branch, CancellationToken cancellationToken);

	/// <summary>The files and lines changed relative to <c>main</c>, committed or not.</summary>
	/// <param name="cancellationToken">A cancellation token.</param>
	Task<IReadOnlyList<ChangedFile>> ChangedFilesAsync(CancellationToken cancellationToken);

	/// <summary>Stages everything and commits it.</summary>
	/// <param name="message">The commit message.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>The new commit's SHA.</returns>
	Task<string> CommitAsync(string message, CancellationToken cancellationToken);

	/// <summary>Pushes the branch to origin.</summary>
	/// <param name="branch">The branch name.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	Task PushBranchAsync(string branch, CancellationToken cancellationToken);

	/// <summary>
	/// Returns the clone to <c>main</c>: discards anything uncommitted, deletes the local branch,
	/// optionally deletes the remote one, and fast-forwards <c>main</c>.
	/// </summary>
	/// <param name="branch">The branch to leave.</param>
	/// <param name="deleteRemoteBranch">Whether to delete the branch on origin as well.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	Task ReturnToMainAsync(string branch, bool deleteRemoteBranch, CancellationToken cancellationToken);
}

/// <summary>Building and testing the clone.</summary>
public interface IAiChangeTests
{
	/// <summary>Builds the clone.</summary>
	/// <param name="cancellationToken">A cancellation token.</param>
	Task<IssueFixCheckStep> BuildAsync(CancellationToken cancellationToken);

	/// <summary>Runs the whole suite.</summary>
	/// <param name="cancellationToken">A cancellation token.</param>
	Task<TestRun> RunAllAsync(CancellationToken cancellationToken);

	/// <summary>Runs only the tests a filter selects.</summary>
	/// <param name="testFilter">A <c>dotnet test --filter</c> expression.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	Task<TestRun> RunOnlyAsync(string testFilter, CancellationToken cancellationToken);

	/// <summary>Runs the suite with coverage.</summary>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>The Cobertura report, or null if none could be produced.</returns>
	Task<string?> CoberturaAsync(CancellationToken cancellationToken);
}

/// <summary>The model-facing work: writing the reproducing test and making the fix.</summary>
public interface IAiChangeWork
{
	/// <summary>
	/// Has the model write a test that reproduces the report.
	/// </summary>
	/// <param name="request">What is being fixed.</param>
	/// <param name="feedback">Why the previous attempt was rejected, or null on the first.</param>
	/// <param name="cancellationToken">Signalled when the user stops the work.</param>
	/// <returns>A test filter that selects the new test, or null if the model gave up.</returns>
	Task<string?> WriteRedTestAsync(AiChangeRequest request, string? feedback, CancellationToken cancellationToken);

	/// <summary>
	/// Has the model change the code.
	/// </summary>
	/// <param name="request">What is being fixed.</param>
	/// <param name="redTestFilter">The reproducing test it must turn green, or null for a rule fix.</param>
	/// <param name="cancellationToken">Signalled when the user stops the work.</param>
	Task<AiFixOutcome> FixAsync(AiChangeRequest request, string? redTestFilter, CancellationToken cancellationToken);
}
