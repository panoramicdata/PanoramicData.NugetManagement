using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="DependabotTriageRunner"/>: which verdicts cause a write, which cause none,
/// and that every write is announced before it happens.
/// </summary>
public class DependabotTriageRunnerTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _repository = "panoramicdata/Athonet.Api";

	/// <summary>A write port that records every call, in order, instead of making it.</summary>
	private sealed class RecordingWriteApi : IGitHubWriteApi
	{
		public List<string> Calls { get; } = [];

		public List<(int Number, string Body)> Comments { get; } = [];

		public List<int> Closed { get; } = [];

		public List<(string Title, string Body)> Created { get; } = [];

		public Task<int> CreateIssueAsync(
			string owner, string name, string title, string body,
			IReadOnlyList<string> labels, CancellationToken cancellationToken)
		{
			Calls.Add($"create:{owner}/{name}");
			Created.Add((title, body));
			return Task.FromResult(1);
		}

		public Task UpdateIssueBodyAsync(
			string owner, string name, int number, string body, CancellationToken cancellationToken)
		{
			Calls.Add($"update:{number}");
			return Task.CompletedTask;
		}

		public Task CommentAsync(
			string owner, string name, int number, string body, CancellationToken cancellationToken)
		{
			Calls.Add($"comment:{number}");
			Comments.Add((number, body));
			return Task.CompletedTask;
		}

		public Task ClosePullRequestAsync(
			string owner, string name, int number, CancellationToken cancellationToken)
		{
			Calls.Add($"close:{number}");
			Closed.Add(number);
			return Task.CompletedTask;
		}
	}

	private sealed class NoOpenIssues : IGitHubIssueApi
	{
		public Task<IReadOnlyList<GitHubOpenItem>> GetOpenItemsAsync(
			string owner, string name, CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyList<GitHubOpenItem>>([]);

		public Task<IReadOnlyList<GitHubIssueComment>> GetRepositoryCommentsPageAsync(
			string owner, string name, int pageNumber, CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyList<GitHubIssueComment>>([]);

		public Task<IReadOnlyList<GitHubIssueComment>> GetCommentsForItemAsync(
			string owner, string name, int issueNumber, CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyList<GitHubIssueComment>>([]);
	}

	private static DependabotTriage Triage(
		int number,
		DependabotVerdict verdict,
		string dependencyName = "github/codeql-action",
		DependencyEcosystem ecosystem = DependencyEcosystem.GitHubActions)
	{
		var url = $"https://github.com/{_repository}/pull/{number}";

		var issue = new RepositoryIssue
		{
			Number = number,
			Title = $"Bump {dependencyName} from 2 to 4",
			IsPullRequest = true,
			HtmlUrl = url,
			AuthorLogin = "dependabot[bot]",
			CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
		};

		var proposal = verdict == DependabotVerdict.Unrecognised
			? null
			: new DependabotProposal(
				number,
				new DependencyRef(ecosystem, dependencyName),
				"2",
				"4",
				null,
				url);

		return new DependabotTriage(issue, proposal, verdict, "because this test says so", null);
	}

	/// <summary>
	/// A runner, the write port it records into, and the log it announces through. The runner takes the
	/// ports per call, so a test hands the same recording pair to every invocation.
	/// </summary>
	private sealed record Subject(
		DependabotTriageRunner Runner,
		RecordingWriteApi Write,
		List<string> Log)
	{
		public Task<DependabotTriageOutcome> RunAsync(params DependabotTriage[] triages)
			=> Runner.RunAsync(
				new NoOpenIssues(),
				Write,
				_repository,
				triages,
				Log.Add,
				TestContext.Current.CancellationToken);
	}

	private static Subject NewSubject()
	{
		var write = new RecordingWriteApi();

		var runner = new DependabotTriageRunner(
			new UncoveredDependencyIssueService("panoramicdata/PanoramicData.NugetManagement"));

		return new Subject(runner, write, []);
	}

	[Fact]
	public async Task AlreadySatisfied_IsCommentedOnThenClosed()
	{
		var subject = NewSubject();

		await subject.RunAsync(Triage(3, DependabotVerdict.AlreadySatisfied));

		subject.Write.Calls.Should().Equal(["comment:3", "close:3"],
			"the explanation has to be in place before the pull request is closed, or a human "
			+ "finding it closed has nothing to read");
	}

	[Fact]
	public async Task ClosingComment_CarriesTheReasonAndTheMarker()
	{
		var subject = NewSubject();

		await subject.RunAsync(Triage(3, DependabotVerdict.AlreadySatisfied));

		var comment = subject.Write.Comments.Should().ContainSingle().Subject;
		comment.Body.Should().Contain("because this test says so");
		comment.Body.Should().Contain(DependabotTriageRunner.ClosedMarker);
	}

	[Fact]
	public async Task EveryWrite_IsAnnouncedBeforeItHappens()
	{
		var subject = NewSubject();

		await subject.RunAsync(Triage(3, DependabotVerdict.AlreadySatisfied));

		subject.Log.Should().Contain(line => line.Contains("#3") && line.Contains("Closing"),
			"the work item's output is the audit trail for a GitHub mutation");
	}

	[Fact]
	public async Task Unrecognised_IsLeftEntirelyAlone()
	{
		var subject = NewSubject();

		await subject.RunAsync(Triage(1, DependabotVerdict.Unrecognised));

		subject.Write.Calls.Should().BeEmpty("not closed, and no issue raised");
	}

	[Fact]
	public async Task ValidCovered_IsLeftToTheFixPipeline()
	{
		var subject = NewSubject();

		await subject.RunAsync(Triage(3, DependabotVerdict.ValidCovered));

		subject.Write.Calls.Should().BeEmpty(
			"a remediation will move it, and the next pass then finds it already satisfied");
	}

	[Fact]
	public async Task ValidUncovered_RaisesTheGapIssueAndClosesNothing()
	{
		var subject = NewSubject();

		await subject.RunAsync(Triage(5, DependabotVerdict.ValidUncovered));

		subject.Write.Created.Should().ContainSingle()
			.Which.Title.Should().Be("No auto-remediation for github-actions: github/codeql-action");
		subject.Write.Closed.Should().BeEmpty("the pull request is still worth merging by hand");
	}

	[Fact]
	public async Task TwoUncoveredPullRequestsForOneDependency_RaiseOneIssue()
	{
		var subject = NewSubject();

		await subject.RunAsync(Triage(5, DependabotVerdict.ValidUncovered), Triage(6, DependabotVerdict.ValidUncovered));

		subject.Write.Created.Should().ContainSingle();
		subject.Write.Created.Single().Body.Should()
			.Contain("/pull/5").And.Contain("/pull/6", "both sightings are evidence for the one gap");
	}

	[Fact]
	public async Task TwoUncoveredDependencies_RaiseAnIssueEach()
	{
		var subject = NewSubject();

		await subject.RunAsync(Triage(5, DependabotVerdict.ValidUncovered), Triage(7, DependabotVerdict.ValidUncovered, "SomePackage", DependencyEcosystem.NuGet));

		subject.Write.Created.Select(c => c.Title).Should().BeEquivalentTo([
			"No auto-remediation for github-actions: github/codeql-action",
			"No auto-remediation for nuget: SomePackage"
		]);
	}

	[Fact]
	public async Task TheOutcomeCountsEachVerdict()
	{
		var subject = NewSubject();

		var outcome = await subject.RunAsync(Triage(1, DependabotVerdict.Unrecognised), Triage(3, DependabotVerdict.AlreadySatisfied), Triage(4, DependabotVerdict.ValidCovered), Triage(5, DependabotVerdict.ValidUncovered));

		outcome.Closed.Should().Be(1);
		outcome.Covered.Should().Be(1);
		outcome.Uncovered.Should().Be(1);
		outcome.Unrecognised.Should().Be(1);
	}

	[Fact]
	public async Task ClosingTheSamePullRequestTwiceInOneProcess_CommentsOnce()
	{
		var subject = NewSubject();
		var triage = Triage(3, DependabotVerdict.AlreadySatisfied);

		await subject.RunAsync(triage);
		await subject.RunAsync(triage);

		subject.Write.Comments.Should().ContainSingle(
			"a closed pull request leaves the open list, so a second pass should not normally see it "
			+ "at all — and if it does, it must not be commented on again");
	}
}
