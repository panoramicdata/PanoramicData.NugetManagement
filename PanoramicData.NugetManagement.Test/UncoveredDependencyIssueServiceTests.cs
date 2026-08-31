using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="UncoveredDependencyIssueService"/>: one issue per uncovered dependency in
/// this application's own repository, appended to as more repositories hit the same gap, and never
/// duplicated — including when several repository lanes report the same gap at once.
/// </summary>
public class UncoveredDependencyIssueServiceTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _targetRepository = "panoramicdata/PanoramicData.NugetManagement";

	private static readonly DependencyRef _codeQl =
		new(DependencyEcosystem.GitHubActions, "github/codeql-action");

	/// <summary>
	/// A read port answering with a fixed set of open issues, so a test can state "this gap has
	/// already been raised" without writing anything first.
	/// </summary>
	private sealed class FakeReadApi(params GitHubOpenItem[] openItems) : IGitHubIssueApi
	{
		public Task<IReadOnlyList<GitHubOpenItem>> GetOpenItemsAsync(
			string owner, string name, CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyList<GitHubOpenItem>>(openItems);

		public Task<IReadOnlyList<GitHubIssueComment>> GetRepositoryCommentsPageAsync(
			string owner, string name, int pageNumber, CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyList<GitHubIssueComment>>([]);

		public Task<IReadOnlyList<GitHubIssueComment>> GetCommentsForItemAsync(
			string owner, string name, int issueNumber, CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyList<GitHubIssueComment>>([]);
	}

	/// <summary>A write port that records every call instead of making it.</summary>
	private sealed class RecordingWriteApi : IGitHubWriteApi
	{
		public List<(string Repository, string Title, string Body)> Created { get; } = [];

		public List<(int Number, string Body)> BodyUpdates { get; } = [];

		public List<int> ClosedIssues { get; } = [];

		public Task<int> CreateIssueAsync(
			string owner, string name, string title, string body,
			IReadOnlyList<string> labels, CancellationToken cancellationToken)
		{
			lock (Created)
			{
				Created.Add(($"{owner}/{name}", title, body));
				return Task.FromResult(100 + Created.Count);
			}
		}

		public Task UpdateIssueBodyAsync(
			string owner, string name, int number, string body, CancellationToken cancellationToken)
		{
			lock (BodyUpdates)
			{
				BodyUpdates.Add((number, body));
			}

			return Task.CompletedTask;
		}

		public Task CommentAsync(
			string owner, string name, int number, string body, CancellationToken cancellationToken)
			=> Task.CompletedTask;

		public Task ClosePullRequestAsync(
			string owner, string name, int number, CancellationToken cancellationToken)
			=> Task.CompletedTask;

		public Task CloseIssueAsync(
			string owner, string name, int number, CancellationToken cancellationToken)
		{
			ClosedIssues.Add(number);
			return Task.CompletedTask;
		}
	}

	private static UncoveredDependencySighting Sighting(
		string repository = "panoramicdata/Athonet.Api",
		int number = 5)
		=> new(
			repository,
			number,
			"2",
			"4",
			$"https://github.com/{repository}/pull/{number}");

	private static GitHubOpenItem ExistingIssue(int number, string body)
		=> new(
			number,
			UncoveredDependencyIssueService.TitleFor(_codeQl),
			false,
			$"https://github.com/{_targetRepository}/issues/{number}",
			"davidbond",
			new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
			body);

	private static UncoveredDependencyIssueService Service() => new(_targetRepository);

	[Fact]
	public void TitleFor_NamesTheEcosystemAndTheDependency()
		=> UncoveredDependencyIssueService.TitleFor(_codeQl)
			.Should().Be("No auto-remediation for github-actions: github/codeql-action");

	[Fact]
	public void MarkerFor_IsStableAndIdentifiesTheDependency()
	{
		var marker = UncoveredDependencyIssueService.MarkerFor(_codeQl);

		marker.Should().Be("<!-- nugetmgmt:uncovered:github-actions/github/codeql-action -->");
		UncoveredDependencyIssueService
			.MarkerFor(new DependencyRef(DependencyEcosystem.GitHubActions, "GitHub/CodeQL-Action"))
			.Should().Be(marker, "case must not split one gap into two issues");
	}

	[Fact]
	public async Task GapNotYetRaised_CreatesOneIssueCarryingTheMarkerAndTheEvidence()
	{
		var write = new RecordingWriteApi();

		await Service()
			.ReportAsync(new FakeReadApi(), write, _codeQl, [Sighting()], TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		var created = write.Created.Should().ContainSingle().Subject;
		created.Repository.Should().Be(_targetRepository);
		created.Title.Should().Be("No auto-remediation for github-actions: github/codeql-action");
		created.Body.Should().Contain(UncoveredDependencyIssueService.MarkerFor(_codeQl));
		created.Body.Should().Contain("panoramicdata/Athonet.Api");
		created.Body.Should().Contain("https://github.com/panoramicdata/Athonet.Api/pull/5");
	}

	[Fact]
	public async Task GapAlreadyRaised_NewSighting_AppendsItToTheExistingIssue()
	{
		var write = new RecordingWriteApi();
		var read = new FakeReadApi(ExistingIssue(42, await BodyOf(Sighting()).ConfigureAwait(true)));

		await Service()
			.ReportAsync(
				read,
				write,
				_codeQl,
				[Sighting("panoramicdata/Highlight.Api", 11)],
				TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		write.Created.Should().BeEmpty("the gap is one issue, however many repositories hit it");
		var update = write.BodyUpdates.Should().ContainSingle().Subject;
		update.Number.Should().Be(42);
		update.Body.Should().Contain("panoramicdata/Highlight.Api", "the new evidence is added");
		update.Body.Should().Contain("panoramicdata/Athonet.Api", "and the old evidence is kept");
	}

	[Fact]
	public async Task GapAlreadyRaised_SameSighting_WritesNothingAtAll()
	{
		var write = new RecordingWriteApi();
		var read = new FakeReadApi(ExistingIssue(42, await BodyOf(Sighting()).ConfigureAwait(true)));

		await Service()
			.ReportAsync(read, write, _codeQl, [Sighting()], TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		write.Created.Should().BeEmpty();
		write.BodyUpdates.Should().BeEmpty("re-running triage must not churn the issue");
	}

	[Fact]
	public async Task AnUnrelatedOpenIssue_DoesNotCountAsTheGapBeingRaised()
	{
		var write = new RecordingWriteApi();
		var read = new FakeReadApi(ExistingIssue(7, "Something else entirely, with no marker."));

		await Service()
			.ReportAsync(read, write, _codeQl, [Sighting()], TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		write.Created.Should().ContainSingle();
	}

	[Fact]
	public async Task NoSightings_WritesNothing()
	{
		var write = new RecordingWriteApi();

		await Service()
			.ReportAsync(new FakeReadApi(), write, _codeQl, [], TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		write.Created.Should().BeEmpty();
	}

	[Fact]
	public async Task ManyLanesReportingTheSameGapAtOnce_CreateExactlyOneIssue()
	{
		var write = new RecordingWriteApi();
		var read = new FakeReadApi();
		var service = Service();

		await Task.WhenAll(Enumerable
				.Range(0, 8)
				.Select(i => service.ReportAsync(
					read,
					write,
					_codeQl,
					[Sighting($"panoramicdata/Repo{i}", i + 1)],
					TestContext.Current.CancellationToken)))
			.ConfigureAwait(true);

		write.Created.Should().ContainSingle(
			"triage runs on many repository lanes at once, and the gap is still one issue — the issue "
			+ "list GitHub returns does not reflect a create immediately, so the service has to "
			+ "remember what it raised rather than trusting the lookup");

		write.BodyUpdates.Should().HaveCount(7, "the other seven sightings are added as evidence");
		write.BodyUpdates[^1].Body.Should().Contain("panoramicdata/Repo0")
			.And.Contain("panoramicdata/Repo7", "every lane's evidence survives to the final body");
	}

	/// <summary>
	/// The body the service itself would write for a sighting, so that "already raised" is stated in
	/// the service's own terms rather than in a hand-written imitation of them.
	/// </summary>
	private static async Task<string> BodyOf(UncoveredDependencySighting sighting)
	{
		var write = new RecordingWriteApi();

		await Service()
			.ReportAsync(new FakeReadApi(), write, _codeQl, [sighting], TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		return write.Created.Single().Body;
	}
}
