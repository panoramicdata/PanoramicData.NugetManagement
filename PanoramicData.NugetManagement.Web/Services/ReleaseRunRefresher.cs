using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Brings a row's release run up to date from GitHub, alongside the published version.
/// </summary>
/// <remarks>
/// Read at assessment time for the same reason the published version is: the pair is compared
/// minutes after a tag is pushed, and a run read at some earlier point says nothing useful about the
/// release happening now. Kept separate from <see cref="PublishedVersionRefresher"/> because one
/// asks nuget.org and the other GitHub, and either can fail without the other's answer becoming
/// worthless.
/// </remarks>
public class ReleaseRunRefresher
{
	private readonly IReleaseRunSource _source;

	/// <summary>
	/// Initializes a new instance of the <see cref="ReleaseRunRefresher"/> class.
	/// </summary>
	/// <param name="source">Reads the CI run for a tag.</param>
	public ReleaseRunRefresher(IReleaseRunSource source)
	{
		_source = source;
	}

	/// <summary>
	/// Re-reads the run for the row's newest tag, leaving it unknown if it cannot be read.
	/// </summary>
	/// <param name="row">The repository whose release run should be refreshed.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	public async Task RefreshAsync(RepositoryDashboardRow row, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(row.LatestTag))
		{
			row.ReleaseRun = null;
			return;
		}

		try
		{
			// Assigned whatever the answer is, including null: a run left over from the previous
			// assessment would let CI-13 keep reporting a failure that is no longer the newest tag's.
			row.ReleaseRun = await _source
				.GetReleaseRunAsync(row.RepositoryFullName, row.LatestTag, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// An unreachable GitHub must not read as a failed release. Both rules treat an unknown
			// run as no evidence either way, so CI-11 reports the unpublished tag and CI-13 stays
			// quiet — which is what they did before any of this existed.
			row.ReleaseRun = null;
		}
	}
}
