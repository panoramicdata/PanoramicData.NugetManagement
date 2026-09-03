using Octokit;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Reads the CI run for a repository's release tag.
/// </summary>
public interface IReleaseRunSource
{
	/// <summary>
	/// Gets the newest workflow run for a tag, or null when the tag has no run.
	/// </summary>
	/// <param name="repositoryFullName">The repository's full name (e.g. "panoramicdata/HaloPsa.Api").</param>
	/// <param name="tag">The tag whose run is wanted.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	Task<ReleaseRun?> GetReleaseRunAsync(string repositoryFullName, string tag, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IReleaseRunSource"/>, reading GitHub Actions runs for the tag ref.
/// </summary>
/// <remarks>
/// A tag push starts a run whose "branch" is the tag, which is what makes this a single request: the
/// newest run for that ref is the release run. What it is used for is telling three states apart —
/// in flight, published but not yet indexed, and failed — that the version numbers alone report
/// identically, and always as the last one.
/// </remarks>
public class ReleaseRunService : IReleaseRunSource
{
	private readonly IGitHubClient _github;
	private readonly ILogger<ReleaseRunService> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="ReleaseRunService"/> class.
	/// </summary>
	/// <param name="github">The GitHub client.</param>
	/// <param name="logger">The logger.</param>
	public ReleaseRunService(IGitHubClient github, ILogger<ReleaseRunService> logger)
	{
		_github = github;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<ReleaseRun?> GetReleaseRunAsync(
		string repositoryFullName,
		string tag,
		CancellationToken cancellationToken)
	{
		var parts = repositoryFullName.Split('/');
		if (parts.Length != 2)
		{
			return null;
		}

		var runs = await _github
			.Actions
			.Workflows
			.Runs
			.List(parts[0], parts[1], new WorkflowRunsRequest { Branch = tag }, new ApiOptions { PageSize = 5, PageCount = 1 })
			.ConfigureAwait(false);

		var run = runs.WorkflowRuns
			.OrderByDescending(candidate => candidate.CreatedAt)
			.FirstOrDefault();

		if (run is null)
		{
			_logger.LogInformation("No workflow run found for {Repository} tag {Tag}.", repositoryFullName, tag);
			return null;
		}

		// StringValue is what GitHub actually sent; Value is Octokit's reading of it, which for an
		// unknown conclusion is not the same thing. ReleaseRunFactory does the reading.
		return ReleaseRunFactory.From(
			tag,
			run.Id,
			run.Status.StringValue,
			run.Conclusion?.StringValue,
			run.HtmlUrl,
			run.RunStartedAt == default ? run.CreatedAt : run.RunStartedAt,
			run.UpdatedAt);
	}
}
