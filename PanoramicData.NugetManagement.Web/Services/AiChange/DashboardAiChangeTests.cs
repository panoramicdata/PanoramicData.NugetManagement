using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// <see cref="IAiChangeTests"/> over the dashboard's build and test plumbing.
/// </summary>
/// <remarks>
/// Every run of the repository's tests is sandboxed (see <see cref="TestSandbox"/>): these are tests a
/// model wrote, and they should see CI's environment, not this machine's credentials.
/// </remarks>
/// <param name="dashboard">The dashboard, for building and running the suite.</param>
/// <param name="repos">The local repository service, for filtered and coverage runs.</param>
/// <param name="row">The repository's row.</param>
/// <param name="repoIdentity">The repository identity the local service knows it by.</param>
/// <param name="knownFailures">
/// Tests that already failed here, in the sandbox, before the change (for example ones that need
/// credentials the sandbox withholds). The suite counts as green if only these fail. Null or empty is strict.
/// </param>
public sealed class DashboardAiChangeTests(
	DashboardService dashboard,
	LocalRepoService repos,
	RepositoryDashboardRow row,
	string repoIdentity,
	IReadOnlySet<string>? knownFailures = null) : IAiChangeTests
{
	/// <summary>
	/// Whether a full run counts as green once failures that were already there are set aside.
	/// </summary>
	/// <param name="exitedGreen">Whether the run itself passed.</param>
	/// <param name="failedNow">The tests that failed in this run.</param>
	/// <param name="known">The tests that failed before the change.</param>
	/// <remarks>
	/// "No regressions" means nothing new broke, not that the repository was perfect to begin with. A run
	/// that failed without naming a failing test, such as one that did not build, is never excused.
	/// </remarks>
	public static bool PassedGivenBaseline(bool exitedGreen, IReadOnlySet<string> failedNow, IReadOnlySet<string> known)
		=> exitedGreen || (failedNow.Count > 0 && failedNow.IsSubsetOf(known));

	/// <inheritdoc />
	public async Task<IssueFixCheckStep> BuildAsync(CancellationToken cancellationToken)
	{
		var lines = new List<string>();
		await dashboard.BuildAsync(row, lines.Add, cancellationToken).ConfigureAwait(false);

		return new IssueFixCheckStep(row.Status == PackageStatus.BuildSucceeded, string.Join('\n', lines));
	}

	/// <inheritdoc />
	public async Task<TestRun> RunAllAsync(CancellationToken cancellationToken)
	{
		var lines = new List<string>();
		await dashboard.RunTestsAsync(row, lines.Add, cancellationToken, sandbox: true).ConfigureAwait(false);

		var output = string.Join('\n', lines);
		var passed = PassedGivenBaseline(
			row.Status == PackageStatus.TestsPassed,
			CoverageMeasurement.ParseFailedTests(output),
			knownFailures ?? new HashSet<string>());

		return new TestRun(passed, output);
	}

	/// <inheritdoc />
	public async Task<TestRun> RunOnlyAsync(string testFilter, CancellationToken cancellationToken)
	{
		var path = repos.GetLocalPath(repoIdentity);
		var environment = TestSandbox.Overrides();
		var passed = true;
		var output = new List<string>();

		foreach (var (projectPath, _) in LocalRepoService.GetConfiguredTestProjects(path))
		{
			var (ok, text) = await LocalRepoService
				.RunCommandWithStreamingAsync(path, "dotnet", ["test", projectPath, "--no-restore", "--filter", testFilter], null, cancellationToken, environment)
				.ConfigureAwait(false);

			passed &= ok;
			output.Add(text);
		}

		return new TestRun(passed, string.Join('\n', output));
	}

	/// <inheritdoc />
	public Task<string?> CoberturaAsync(CancellationToken cancellationToken)
		=> repos.RunTestsWithCoverageAsync(repoIdentity, cancellationToken);
}
