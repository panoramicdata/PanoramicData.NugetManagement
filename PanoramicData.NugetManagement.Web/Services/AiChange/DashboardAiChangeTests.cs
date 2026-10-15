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
public sealed class DashboardAiChangeTests(
	DashboardService dashboard,
	LocalRepoService repos,
	RepositoryDashboardRow row,
	string repoIdentity) : IAiChangeTests
{
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

		return new TestRun(row.Status == PackageStatus.TestsPassed, string.Join('\n', lines));
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
