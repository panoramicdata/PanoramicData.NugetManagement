using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Decides whether a discovered package's repository is ours to govern.
/// </summary>
/// <remarks>
/// The population comes from NuGet — every package matching <c>owner:&lt;org&gt;</c> — and each
/// package's repository is derived from its nuspec. Those two facts answer different questions:
/// <c>owner:</c> is who owns the <em>package</em>. A repackage is ours to publish and somebody
/// else's to host, and Vizor.ECharts.Net80 is exactly that — its nuspec correctly declares
/// <c>datahint-eu/vizor-echarts</c>. Until this gate existed the derived location was taken on
/// trust, so that repository was cloned into the app's clone root and assessed against our rules.
///
/// Nothing here guesses. A package whose nuspec names the wrong repository, or none at all, is not
/// governed and says why; the fix belongs in the nuspec.
/// </remarks>
public static class GovernanceScope
{
	/// <summary>
	/// Why a repository is not ours to govern, or null when it is.
	/// </summary>
	/// <param name="repositoryFullName">The repository as <c>owner/name</c>, or null when the nuspec declared none.</param>
	/// <param name="organizations">The organisations under management.</param>
	public static string? ReasonNotGoverned(string? repositoryFullName, IReadOnlyList<string> organizations)
	{
		if (string.IsNullOrWhiteSpace(repositoryFullName))
		{
			return "The package declares no repository in its nuspec.";
		}

		var segments = repositoryFullName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (segments.Length < 2)
		{
			return $"The package declares '{repositoryFullName}', which names no owner.";
		}

		var owner = segments[^2];
		return organizations.Contains(owner, StringComparer.OrdinalIgnoreCase)
			? null
			: $"The nuspec declares {repositoryFullName}, which is not one of our organisations.";
	}

	/// <summary>
	/// Records on a row whether its repository is ours, and strips the local state of one that is not.
	/// </summary>
	/// <remarks>
	/// The clone facts are cleared rather than left alone: a row that reached here already claiming a
	/// checkout was governed under the older rules, and every button that offers to build, commit or
	/// push reads exactly those fields.
	/// </remarks>
	/// <param name="row">The repository row to record the verdict on.</param>
	/// <param name="organizations">The organisations under management.</param>
	public static void Apply(RepositoryDashboardRow row, IReadOnlyList<string> organizations)
	{
		var reason = ReasonNotGoverned(row.RepositoryFullName, organizations);
		row.NotGovernedReason = reason;
		row.IsGoverned = reason is null;

		if (reason is null)
		{
			return;
		}

		row.Status = PackageStatus.NotGoverned;

		// Findings survive in the cache, and a row that reached here may have been assessed while it was
		// still thought to be ours. Every count on the estate — failures, criticals, health — reads
		// through Assessment, so leaving it in place would keep somebody else's repository in the totals.
		row.Assessment = null;
		// Issues count toward the same totals as rules, so leaving them would keep somebody else's
		// repository contributing failures after it stopped being ours.
		row.OpenIssues = [];
		row.IsClonedLocally = false;
		row.LocalPath = null;
		row.SlnxPath = null;
		row.CurrentBranch = null;
		row.IsWorkingTreeClean = null;
		row.IsSyncedWithOrigin = null;
		row.SyncStatusCheckedAtUtc = null;
	}
}
