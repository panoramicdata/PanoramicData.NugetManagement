using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Remediations;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that the catalogue of work and the code that runs it cannot drift apart.
/// </summary>
/// <remarks>
/// The bodies themselves are covered by the service tests they call into. What is not covered
/// anywhere else — and what a closed catalogue makes checkable at all — is that every kind has
/// somewhere to go.
/// </remarks>
public class WorkExecutorsTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void SupportedKinds_CoversEveryWorkKind()
	{
		var missing = Enum.GetValues<WorkKind>()
			.Where(kind => !WorkExecutors.SupportedKinds.Contains(kind))
			.ToList();

		missing.Should().BeEmpty(
			"a WorkKind with no executor would queue work that can never run");
	}

	[Fact]
	public void SupportedKinds_InventsNothing()
		=> WorkExecutors.SupportedKinds.Should().OnlyContain(kind => Enum.IsDefined(kind));
}

/// <summary>
/// Pins the specific defect a controller ruling found in Task 9's review: a failed build used to be
/// read as a success, because the badge state was inferred from <see cref="WorkItemState"/> — which a
/// failed build never leaves as anything but <see cref="WorkItemState.Completed"/>, since
/// <see cref="WorkExecutors"/>'s build/test/push bodies log a failure and return rather than throwing.
/// </summary>
/// <remarks>
/// Exercised through the real <see cref="WorkExecutors.ExecuteAsync"/> and a real, empty local
/// checkout — a build that fails because there is nothing to build is what makes this a test of the
/// executor's own outcome reporting rather than of a fake standing in for it. It costs one real
/// <c>dotnet build</c> invocation, which fails in well under a second with nothing to restore.
/// </remarks>
public sealed class WorkExecutorsBuildOutcomeTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private const string Repository = "panoramicdata/does-not-exist";

	private readonly string _root = Directory.CreateTempSubdirectory("nugetmgmt-workexec-").FullName;

	[Fact]
	public async Task BuildAsync_FailedBuild_StatesFailureRatherThanLeavingItToBeInferred()
	{
		// An empty directory: dotnet build fails immediately ("no project or solution file") without
		// needing a real clone, which is all this test needs — a build that genuinely fails.
		var repoDirectory = Path.Combine(_root, "panoramicdata", "does-not-exist");
		Directory.CreateDirectory(repoDirectory);

		var executors = CreateExecutors(out var cache);
		cache.SetRows(
		[
			new RepositoryDashboardRow
			{
				RepositoryFullName = Repository,
				Organization = "panoramicdata",
				Packages = [new() { PackageId = "Does.Not.Exist", LatestVersion = "1.0.0" }]
			}
		]);

		var item = new WorkItem
		{
			Id = "1",
			Title = "Build",
			Descriptor = WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", Repository),
			DedupKey = $"build:{Repository}",
			Step = WorkflowStep.Build
		};

		await executors.ExecuteAsync(item, new Progress<string>(), CancellationToken.None);

		// The fact under test: a failed build is STATED as failed, not read off a State that a
		// non-throwing failure never distinguishes from success.
		item.Succeeded.Should().BeFalse(
			"a failed build must not be indistinguishable from a successful one to whatever reads Succeeded");
		item.State.Should().Be(WorkItemState.Pending, "ExecuteAsync itself never advances State — that is WorkRunnerService's job");
	}

	/// <summary>
	/// Pins the round-2 regression: <see cref="LocalRepoService.RunCommandWithStreamingAsync"/> kills
	/// the process and rethrows <see cref="OperationCanceledException"/> when the token is signalled,
	/// and <see cref="WorkExecutors.BuildAsync"/> used to let that fall into its general
	/// <c>catch (Exception ex)</c>, which states <c>Succeeded = false</c> — turning a stop into a
	/// recorded failure and arming "Fix with AI" for work the user chose to end, which is exactly what
	/// addendum A9.1 said must never happen. The executor must now rethrow the cancellation ahead of
	/// that catch, leaving <see cref="WorkItem.Succeeded"/> untouched (null).
	/// </summary>
	[Fact]
	public async Task BuildAsync_CancelledBuild_DoesNotStateFailure()
	{
		var repoDirectory = Path.Combine(_root, "panoramicdata", "does-not-exist");
		Directory.CreateDirectory(repoDirectory);

		var executors = CreateExecutors(out var cache);
		cache.SetRows(
		[
			new RepositoryDashboardRow
			{
				RepositoryFullName = Repository,
				Organization = "panoramicdata",
				Packages = [new() { PackageId = "Does.Not.Exist", LatestVersion = "1.0.0" }]
			}
		]);

		var item = new WorkItem
		{
			Id = "1",
			Title = "Build",
			Descriptor = WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", Repository),
			DedupKey = $"build:{Repository}",
			Step = WorkflowStep.Build
		};

		// Pre-cancelled rather than cancelled mid-flight: WaitForExitAsync observes it as soon as it is
		// awaited, which is deterministic — a race to cancel a real dotnet build process mid-run would
		// not be.
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();

		var act = () => executors.ExecuteAsync(item, new Progress<string>(), cancellation.Token);

		await act.Should().ThrowAsync<OperationCanceledException>(
			"the executor must rethrow so the runner marks the item Cancelled rather than swallowing the stop");

		item.Succeeded.Should().BeNull(
			"a stop is not a failure — Succeeded must be left exactly as it was, for OnItemCompleted's badge logic to leave the step's badge alone");
	}

	private WorkExecutors CreateExecutors(out DashboardCacheService cache)
	{
		var appSettings = Options.Create(new AppSettings { LocalReposRoot = _root });
		var runtimeSettings = new RuntimeSettingsService(appSettings, NullLogger<RuntimeSettingsService>.Instance);
		var localRepo = new LocalRepoService(runtimeSettings, NullLogger<LocalRepoService>.Instance);
		cache = new DashboardCacheService(
			NullLogger<DashboardCacheService>.Instance,
			Path.Combine(_root, "dashboard-cache.json"));

		var dashboard = new DashboardService(
			new NuGetDiscoveryService(appSettings, new NuspecRepositoryResolver(new NoopHttpClientFactory(), NullLogger<NuspecRepositoryResolver>.Instance), NullLogger<NuGetDiscoveryService>.Instance),
			new PublishedVersionRefresher(new NoopPublishedVersionSource()),
			cache,
			localRepo,
			new RemediationRegistry(),
			new RegressionGuardService(localRepo, NullLogger<RegressionGuardService>.Instance),
			runtimeSettings,
			appSettings,
			NullLogger<DashboardService>.Instance);

		var lanes = new WorkLaneService();
		return new WorkExecutors(
			dashboard,
			cache,
			localRepo,
			runtimeSettings,
			new WorkFanOut(lanes),
			new GitHubTokenProvider(),
			new HttpContextAccessor(),
			NullLogger<WorkExecutors>.Instance);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// A locked temp file (e.g. an obj/ lock left by the failed build) is not worth failing the
			// test that produced it.
		}

		GC.SuppressFinalize(this);
	}

	/// <summary>Never called: <see cref="DashboardService.BuildAsync"/> does not touch NuGet.</summary>
	private sealed class NoopHttpClientFactory : IHttpClientFactory
	{
		public HttpClient CreateClient(string name) => new();
	}

	/// <summary>Never called: <see cref="DashboardService.BuildAsync"/> does not read published versions.</summary>
	private sealed class NoopPublishedVersionSource : IPublishedVersionSource
	{
		public Task<string?> GetLatestPublishedVersionAsync(string packageId, CancellationToken cancellationToken)
			=> Task.FromResult<string?>(null);
	}
}
