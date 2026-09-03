using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// What a repository's local git facts add up to: whether it still matches origin as far as anyone
/// knows, and whether there is anything at all to commit or push.
/// </summary>
/// <remarks>
/// Static and here rather than in the page for the same reason as <see cref="RepositoryStateBadges"/>:
/// these answers gate a button and label a badge, and the two must agree — a greyed-out Commit &amp;
/// Push beside a badge saying there is work to push is worse than either alone. One function decides
/// it, and it can be unit tested.
/// </remarks>
public static class RepositoryGitState
{
	/// <summary>
	/// How long a confirmed-in-sync answer is treated as still true. Long enough to cover working
	/// through the toolbar on one repository, short enough that a stale answer does not block a pull.
	/// </summary>
	public static readonly TimeSpan SyncStatusTrustedFor = TimeSpan.FromMinutes(5);

	/// <summary>
	/// Whether this row was confirmed to match origin recently enough to still act on it.
	/// </summary>
	/// <param name="row">The repository.</param>
	/// <param name="nowUtc">The moment to age the answer against.</param>
	/// <remarks>
	/// The age matters because being in sync is not a durable fact — origin can move a moment after the
	/// check, and nothing tells the app when it does. Disabling a button on the bare claim would
	/// eventually strand someone with a control they cannot press and a pull they need, so the claim
	/// expires.
	/// </remarks>
	public static bool IsRecentlyConfirmedInSync(RepositoryDashboardRow row, DateTimeOffset nowUtc)
		=> row.IsSyncedWithOrigin == true
			&& row.SyncStatusCheckedAtUtc is { } checkedAt
			&& nowUtc - checkedAt < SyncStatusTrustedFor;

	/// <summary>
	/// Whether the clone is porcelain: nothing uncommitted, nothing committed but unpushed, and not
	/// known to be behind origin. The state in which Commit &amp; Push has nothing to do.
	/// </summary>
	/// <param name="row">The repository, or null when nothing is selected.</param>
	/// <remarks>
	/// A clean tree alone is not enough — a commit made and not pushed leaves nothing to commit and
	/// something to send — so both local facts have to be positively known, and an unread one fails the
	/// test. That is the safe direction to be wrong in: an enabled Commit &amp; Push on a porcelain
	/// clone costs a no-op, while a disabled one on a clone with work pending hides the step the user
	/// needs.
	/// <para>
	/// Both are local reads, kept current by the page's working tree watcher, so this answer needs no
	/// expiry the way <see cref="IsRecentlyConfirmedInSync"/> does. Being behind origin is the one part
	/// that cannot be read locally, and it is only excluded where it is positively known — a clone that
	/// has never been compared with origin is not held to be out of step with it.
	/// </para>
	/// </remarks>
	public static bool IsPorcelain(RepositoryDashboardRow? row)
		=> row is not null
			&& row.IsClonedLocally
			&& row.IsWorkingTreeClean == true
			&& row.HasUnpushedCommits == false
			&& row.IsSyncedWithOrigin != false;
}
