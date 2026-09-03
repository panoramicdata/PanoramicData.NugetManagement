using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the fact behind the Porcelain badge and the Commit &amp; Push gate: a clone with nothing
/// uncommitted, nothing committed but unpushed, and no known drift behind origin. Both local halves
/// must be positively known, because the cost of being wrong is asymmetric — an enabled button on a
/// porcelain clone wastes a no-op, while a disabled one on a clone with work pending hides the step.
/// </summary>
public class RepositoryGitStateTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

	private static RepositoryDashboardRow Row(
		bool isClonedLocally = true,
		bool? isWorkingTreeClean = true,
		bool? hasUnpushedCommits = false,
		bool? isSyncedWithOrigin = true,
		TimeSpan? syncCheckedAgo = null) => new()
		{
			RepositoryFullName = "panoramicdata/Joker.Api",
			IsClonedLocally = isClonedLocally,
			IsWorkingTreeClean = isWorkingTreeClean,
			HasUnpushedCommits = hasUnpushedCommits,
			IsSyncedWithOrigin = isSyncedWithOrigin,
			SyncStatusCheckedAtUtc = syncCheckedAgo is null ? null : Now - syncCheckedAgo.Value
		};

	[Fact]
	public void IsPorcelain_CleanWithNothingToPush_IsPorcelain()
		=> RepositoryGitState.IsPorcelain(Row()).Should().BeTrue();

	[Fact]
	public void IsPorcelain_DirtyWorkingTree_IsNot()
		=> RepositoryGitState.IsPorcelain(Row(isWorkingTreeClean: false)).Should().BeFalse();

	[Fact]
	public void IsPorcelain_CleanlinessUnread_IsNot()
		=> RepositoryGitState.IsPorcelain(Row(isWorkingTreeClean: null)).Should().BeFalse();

	/// <summary>
	/// The case a clean-tree test alone gets wrong: committed, not pushed, so there is nothing to
	/// commit and something to send.
	/// </summary>
	[Fact]
	public void IsPorcelain_CleanButHoldingUnpushedCommits_IsNot()
		=> RepositoryGitState.IsPorcelain(Row(hasUnpushedCommits: true)).Should().BeFalse();

	[Fact]
	public void IsPorcelain_UnpushedStateUnread_IsNot()
		=> RepositoryGitState.IsPorcelain(Row(hasUnpushedCommits: null)).Should().BeFalse();

	[Fact]
	public void IsPorcelain_KnownBehindOrigin_IsNot()
		=> RepositoryGitState.IsPorcelain(Row(isSyncedWithOrigin: false)).Should().BeFalse();

	/// <summary>
	/// Never compared with origin is not the same as out of step with it: the two local facts still
	/// decide, and the badge's tooltip is what says the sync half is unchecked.
	/// </summary>
	[Fact]
	public void IsPorcelain_NeverComparedWithOrigin_IsStillPorcelain()
		=> RepositoryGitState.IsPorcelain(Row(isSyncedWithOrigin: null)).Should().BeTrue();

	/// <summary>
	/// Unlike the Sync button's gate, this one does not expire: both halves are local reads that only
	/// something done to this checkout can change, and the watcher keeps them current.
	/// </summary>
	[Fact]
	public void IsPorcelain_StaleSyncAnswer_StillPorcelain()
		=> RepositoryGitState
			.IsPorcelain(Row(syncCheckedAgo: RepositoryGitState.SyncStatusTrustedFor + TimeSpan.FromHours(1)))
			.Should().BeTrue();

	[Fact]
	public void IsPorcelain_NotClonedLocally_IsNot()
		=> RepositoryGitState.IsPorcelain(Row(isClonedLocally: false)).Should().BeFalse();

	[Fact]
	public void IsPorcelain_NothingSelected_IsNot()
		=> RepositoryGitState.IsPorcelain(null).Should().BeFalse();

	[Fact]
	public void IsRecentlyConfirmedInSync_WithinTheWindow_IsTrusted()
		=> RepositoryGitState
			.IsRecentlyConfirmedInSync(Row(syncCheckedAgo: RepositoryGitState.SyncStatusTrustedFor - TimeSpan.FromSeconds(1)), Now)
			.Should().BeTrue();

	[Fact]
	public void IsRecentlyConfirmedInSync_PastTheWindow_IsNotTrusted()
		=> RepositoryGitState
			.IsRecentlyConfirmedInSync(Row(syncCheckedAgo: RepositoryGitState.SyncStatusTrustedFor + TimeSpan.FromSeconds(1)), Now)
			.Should().BeFalse();

	[Fact]
	public void IsRecentlyConfirmedInSync_NeverChecked_IsNotTrusted()
		=> RepositoryGitState.IsRecentlyConfirmedInSync(Row(syncCheckedAgo: null), Now).Should().BeFalse();

	[Fact]
	public void IsRecentlyConfirmedInSync_DirtyTreeMakesNoDifference_StillTrusted()
		=> RepositoryGitState
			.IsRecentlyConfirmedInSync(Row(isWorkingTreeClean: false, syncCheckedAgo: TimeSpan.FromSeconds(10)), Now)
			.Should().BeTrue();
}
