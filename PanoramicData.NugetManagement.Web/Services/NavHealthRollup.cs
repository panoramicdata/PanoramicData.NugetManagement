using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Rolls child statuses up to the branch nodes of the navigation tree, so every node shows the worst
/// status of everything beneath it.
/// </summary>
/// <remarks>
/// Unknown is the worst status of all, not the mildest: an unassessed repository could be in any
/// state, so a green organisation node above one would claim something we do not know. Grey therefore
/// wins over red, and a branch only turns green once every repository under it has been assessed and
/// found clean. The one branch that does not take part is Issues, which is coloured by its own
/// children alone — see <see cref="ForIssues"/>.
/// </remarks>
public static class NavHealthRollup
{
	/// <summary>
	/// Ranks a status by how bad it is, worst first. Unknown outranks everything; Pending sits just
	/// below it, because an assessment in flight is equally unknown but is about to resolve itself.
	/// </summary>
	public static int Rank(PackageHealthStatus status) => status switch
	{
		PackageHealthStatus.Unknown => 6,
		PackageHealthStatus.Pending => 5,
		PackageHealthStatus.Error => 4,
		PackageHealthStatus.Warning => 3,
		PackageHealthStatus.Info => 2,
		PackageHealthStatus.Success => 1,
		_ => 6
	};

	/// <summary>
	/// The worst of the given statuses. An empty sequence is Unknown: nothing to roll up is not the
	/// same as nothing wrong.
	/// </summary>
	public static PackageHealthStatus Worst(IEnumerable<PackageHealthStatus> statuses)
	{
		var worst = PackageHealthStatus.Success;
		var any = false;

		foreach (var status in statuses)
		{
			any = true;

			if (Rank(status) > Rank(worst))
			{
				worst = status;
			}
		}

		return any ? worst : PackageHealthStatus.Unknown;
	}

	/// <summary>
	/// The worse of two statuses.
	/// </summary>
	public static PackageHealthStatus Worst(PackageHealthStatus first, PackageHealthStatus second)
		=> Rank(first) >= Rank(second) ? first : second;

	/// <summary>
	/// Maps a rule or category severity onto the shared health status. Critical and Error share red:
	/// the tree distinguishes them by glyph, not by colour.
	/// </summary>
	public static PackageHealthStatus FromSeverity(AssessmentSeverity severity) => severity switch
	{
		AssessmentSeverity.Critical or AssessmentSeverity.Error => PackageHealthStatus.Error,
		AssessmentSeverity.Warning => PackageHealthStatus.Warning,
		_ => PackageHealthStatus.Info
	};

	/// <summary>
	/// The status of an organisation's Repositories branch: the worst of its repositories.
	/// </summary>
	public static PackageHealthStatus ForRepositories(IEnumerable<RepositoryDashboardRow>? rows)
		=> rows is null
			? PackageHealthStatus.Unknown
			: Worst(rows.Select(r => r.HealthStatus));

	/// <summary>
	/// The status of an organisation's Issues branch: the worst severity of its issue categories.
	/// </summary>
	/// <remarks>
	/// Deliberately blind to unassessed repositories, even though their issues are not in the
	/// categories yet. This node is coloured only by what hangs beneath it, so that its colour always
	/// has a visible cause: a grey Issues branch above a row of red and amber categories tells the
	/// reader nothing they can act on, and during a whole-organisation re-assessment — when every row
	/// is briefly unassessed — it would be grey while showing no grey child at all. That the picture
	/// is still being built is said by the spinner on the organisation node and the in-flight
	/// treatment of the issue nodes, not by this colour. Unassessed repositories still grey the
	/// Repositories branch, where they are visible as grey children.
	/// </remarks>
	public static PackageHealthStatus ForIssues(
		IEnumerable<RepositoryDashboardRow>? rows,
		IEnumerable<AssessmentSeverity> categorySeverities)
	{
		if (rows is null)
		{
			return PackageHealthStatus.Unknown;
		}

		var rowList = rows as IReadOnlyCollection<RepositoryDashboardRow> ?? [.. rows];

		// An empty category list means one of two things, and only the rows can tell them apart:
		// everything was assessed and came back clean, or nothing has been assessed yet. The first is
		// green; the second has nothing beneath it and nothing known, so it stays grey.
		if (!rowList.Any(r => r.Assessment is not null))
		{
			return PackageHealthStatus.Unknown;
		}

		return Worst(categorySeverities.Select(FromSeverity).DefaultIfEmpty(PackageHealthStatus.Success));
	}

	/// <summary>
	/// The Bootstrap text colour class for a status. Unknown and Pending are both grey.
	/// </summary>
	public static string ColourClass(PackageHealthStatus status) => status switch
	{
		PackageHealthStatus.Success => "text-success",
		PackageHealthStatus.Error => "text-danger",
		PackageHealthStatus.Warning => "text-warning",
		PackageHealthStatus.Info => "text-info",
		_ => "text-muted"
	};

	/// <summary>
	/// Combines a Font Awesome glyph with the colour class for a status.
	/// </summary>
	public static string Icon(string glyph, PackageHealthStatus status)
		=> $"{glyph} {ColourClass(status)}";
}
