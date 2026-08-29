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
/// found clean.
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
	public static PackageHealthStatus ForRepositories(IEnumerable<PackageDashboardRow>? rows)
		=> rows is null
			? PackageHealthStatus.Unknown
			: Worst(rows.Select(r => r.HealthStatus));

	/// <summary>
	/// The status of an organisation's Issues branch: the worst severity of its issue categories, but
	/// grey while any repository is unassessed — the issue picture is built only from repositories that
	/// were assessed, so an unassessed one means issues we have not seen yet.
	/// </summary>
	public static PackageHealthStatus ForIssues(
		IEnumerable<PackageDashboardRow>? rows,
		IEnumerable<AssessmentSeverity> categorySeverities)
	{
		if (rows is null)
		{
			return PackageHealthStatus.Unknown;
		}

		var rowList = rows as IReadOnlyCollection<PackageDashboardRow> ?? [.. rows];

		if (rowList.Count == 0)
		{
			return PackageHealthStatus.Unknown;
		}

		// Only rows without an assessment can hide issues; an assessed row's failures are already in
		// the categories. Those rows are Unknown or Pending, both of which outrank every severity.
		var unassessed = Worst(rowList
			.Where(r => r.Assessment is null)
			.Select(r => r.HealthStatus)
			.DefaultIfEmpty(PackageHealthStatus.Success));

		return Worst(unassessed, Worst(categorySeverities.Select(FromSeverity).DefaultIfEmpty(PackageHealthStatus.Success)));
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
