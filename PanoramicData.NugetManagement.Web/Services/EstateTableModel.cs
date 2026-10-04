using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// How one column is doing for one repository.
/// </summary>
/// <remarks>
/// Unknown is a state of its own and never reads as good. Nothing about an unchecked repository is
/// reassuring, and a cell that looked clean because it was empty is the most expensive way for the
/// estate to mislead.
/// </remarks>
public enum EstateHealth
{
	/// <summary>Not established: never checked here, or changed since.</summary>
	Unknown,

	/// <summary>Fine.</summary>
	Good,

	/// <summary>Not fine.</summary>
	Bad
}

/// <summary>
/// The worst severity among a repository's failures in one category.
/// </summary>
public enum EstateSeverity
{
	/// <summary>The repository has not been assessed, so nothing is known.</summary>
	Unknown,

	/// <summary>Assessed, with no failing rule in this category.</summary>
	Clean,

	/// <summary>Only informational findings.</summary>
	Info,

	/// <summary>At worst a warning.</summary>
	Warning,

	/// <summary>At least one error or critical finding.</summary>
	Error
}

/// <summary>
/// A column the table can be sorted by.
/// </summary>
public enum EstateSortColumn
{
	/// <summary>What is wrong first: failed build, failed tests, not known, then most issues.</summary>
	Default,

	/// <summary>The repository's name.</summary>
	Repository,

	/// <summary>The total issue count.</summary>
	Issues,

	/// <summary>The current branch.</summary>
	Branch,

	/// <summary>Uncommitted changes.</summary>
	Uncommitted,

	/// <summary>Commits not pushed.</summary>
	Unpushed,

	/// <summary>Sync with origin.</summary>
	Sync,

	/// <summary>The last build.</summary>
	Build,

	/// <summary>The last test run.</summary>
	Test,

	/// <summary>One issue category; the category is passed separately.</summary>
	Category
}

/// <summary>
/// One cell of an issue-class column.
/// </summary>
/// <param name="Category">The class of issue.</param>
/// <param name="Count">How many failing rules, waived and passing ones excluded.</param>
/// <param name="Severity">The worst severity present.</param>
public sealed record EstateCategoryCell(AssessmentCategory Category, int Count, EstateSeverity Severity);

/// <summary>
/// One row of the estate table.
/// </summary>
/// <param name="Source">The row it was built from.</param>
/// <param name="Name">The repository's short name.</param>
/// <param name="FullName">The repository, as "owner/name".</param>
/// <param name="InertReason">Why the row cannot be ticked, or null when it can.</param>
/// <param name="Issues">The total issue count, including stale GitHub issues.</param>
/// <param name="Cells">One cell per category column.</param>
/// <param name="Branch">The current branch, or null when it is not known.</param>
/// <param name="Uncommitted">Whether the working tree is free of uncommitted changes.</param>
/// <param name="Unpushed">Whether there are no commits waiting to be pushed.</param>
/// <param name="Sync">Whether the clone matches origin.</param>
/// <param name="Build">The last build.</param>
/// <param name="Test">The last test run.</param>
public sealed record EstateTableRow(
	RepositoryDashboardRow Source,
	string Name,
	string FullName,
	string? InertReason,
	int Issues,
	IReadOnlyDictionary<AssessmentCategory, EstateCategoryCell> Cells,
	string? Branch,
	EstateHealth Uncommitted,
	EstateHealth Unpushed,
	EstateHealth Sync,
	EstateHealth Build,
	EstateHealth Test)
{
	/// <summary>Whether the row can be ticked.</summary>
	public bool CanSelect => InertReason is null;
}

/// <summary>
/// The estate table, as data: which columns, what each cell holds, what order, and what a filter hides.
/// </summary>
/// <remarks>
/// A separate type rather than logic in the component, for the reason <see cref="ToolbarScope"/> gives:
/// a Razor component cannot be unit tested here, and this is the part worth being sure of.
/// <para>
/// "Ascending" means worst first, for every column where there is a worst: a repository's name sorts
/// A to Z, but a state column puts what is wrong at the top, which is why anyone opens the table.
/// </para>
/// </remarks>
public sealed class EstateTableModel
{
	/// <summary>The issue categories that are columns, in enum order.</summary>
	public required IReadOnlyList<AssessmentCategory> Categories { get; init; }

	/// <summary>The rows to show: filtered, then sorted.</summary>
	public required IReadOnlyList<EstateTableRow> Rows { get; init; }

	/// <summary>How many repositories there are before the filter.</summary>
	public required int TotalCount { get; init; }

	/// <summary>The visible rows that can be ticked, which is what a header checkbox acts on.</summary>
	public IReadOnlyList<string> SelectableNames
		=> [.. Rows.Where(row => row.CanSelect).Select(row => row.FullName)];

	/// <summary>
	/// How many ticked repositories the filter is hiding.
	/// </summary>
	/// <remarks>
	/// A press acts on every ticked row, hidden or not, so the selection line has to say how many it
	/// cannot see rather than let someone believe a press only touches what is on screen.
	/// </remarks>
	/// <param name="selection">What is ticked.</param>
	public int HiddenSelectedCount(RepositorySelection selection)
	{
		var visible = new HashSet<string>(Rows.Select(row => row.FullName), StringComparer.OrdinalIgnoreCase);
		return selection.Names.Count(name => !visible.Contains(name));
	}

	/// <summary>
	/// Builds the table for a set of repositories.
	/// </summary>
	/// <param name="rows">Every repository in the organisation.</param>
	/// <param name="isExcluded">Whether a repository has been excluded from governance.</param>
	/// <param name="filter">Text the name must contain, without regard to case; null or empty shows all.</param>
	/// <param name="sort">The column to sort by.</param>
	/// <param name="descending">Whether to reverse the order.</param>
	/// <param name="sortCategory">The category to sort by when <paramref name="sort"/> is <see cref="EstateSortColumn.Category"/>.</param>
	public static EstateTableModel Build(
		IEnumerable<RepositoryDashboardRow> rows,
		Func<string, bool> isExcluded,
		string? filter = null,
		EstateSortColumn sort = EstateSortColumn.Default,
		bool descending = false,
		AssessmentCategory? sortCategory = null)
	{
		var all = rows.ToList();

		// The columns come from every repository the estate counts, not the filtered ones, so the
		// layout does not shift as a filter is typed.
		var categories = all
			.Where(row => row.IsGoverned && !isExcluded(row.RepositoryFullName) && row.Assessment is not null)
			.SelectMany(row => row.CategorySummaries
				.Where(pair => pair.Value.TotalFailures > 0)
				.Select(pair => pair.Key))
			.Distinct()
			.OrderBy(category => category)
			.ToList();

		var built = all.Select(row => ToRow(row, categories, isExcluded)).ToList();

		IEnumerable<EstateTableRow> visible = built;

		if (!string.IsNullOrWhiteSpace(filter))
		{
			visible = visible.Where(row => row.FullName.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase));
		}

		var ordered = Order(visible, sort, sortCategory).ToList();

		if (descending)
		{
			ordered.Reverse();
		}

		return new EstateTableModel
		{
			Categories = categories,
			Rows = ordered,
			TotalCount = all.Count
		};
	}

	private static EstateTableRow ToRow(
		RepositoryDashboardRow row,
		List<AssessmentCategory> categories,
		Func<string, bool> isExcluded)
	{
		var cells = categories.ToDictionary(category => category, category => Cell(row, category));

		return new EstateTableRow(
			row,
			row.RepositoryName,
			row.RepositoryFullName,
			InertReason(row, isExcluded),
			row.TotalFailures,
			cells,
			row.CurrentBranch,
			Flag(row.IsWorkingTreeClean, goodWhen: true),
			Flag(row.HasUnpushedCommits, goodWhen: false),
			Flag(row.IsSyncedWithOrigin, goodWhen: true),
			row.LastBuildState switch
			{
				RepositoryBuildState.Succeeded => EstateHealth.Good,
				RepositoryBuildState.Failed => EstateHealth.Bad,
				_ => EstateHealth.Unknown
			},
			row.LastTestState switch
			{
				RepositoryTestState.Passed => EstateHealth.Good,
				RepositoryTestState.Failed => EstateHealth.Bad,
				_ => EstateHealth.Unknown
			});
	}

	private static EstateCategoryCell Cell(RepositoryDashboardRow row, AssessmentCategory category)
	{
		if (row.Assessment is null)
		{
			return new EstateCategoryCell(category, 0, EstateSeverity.Unknown);
		}

		if (!row.CategorySummaries.TryGetValue(category, out var summary))
		{
			return new EstateCategoryCell(category, 0, EstateSeverity.Clean);
		}

		var severity = summary.Criticals + summary.Errors > 0
			? EstateSeverity.Error
			: summary.Warnings > 0
				? EstateSeverity.Warning
				: summary.Infos > 0 ? EstateSeverity.Info : EstateSeverity.Clean;

		return new EstateCategoryCell(category, summary.TotalFailures, severity);
	}

	private static string? InertReason(RepositoryDashboardRow row, Func<string, bool> isExcluded)
		=> isExcluded(row.RepositoryFullName)
			? "excluded"
			: !row.IsGoverned
				? "not governed"
				: !row.IsClonedLocally ? "not cloned" : null;

	private static EstateHealth Flag(bool? value, bool goodWhen)
		=> value is null ? EstateHealth.Unknown : value == goodWhen ? EstateHealth.Good : EstateHealth.Bad;

	private static IEnumerable<EstateTableRow> Order(
		IEnumerable<EstateTableRow> rows,
		EstateSortColumn sort,
		AssessmentCategory? category)
	{
		static int WorstFirst(EstateHealth health) => health switch
		{
			EstateHealth.Bad => 0,
			EstateHealth.Unknown => 1,
			_ => 2
		};

		// The default order is one rule, stated once: a failed build, then a failed test, then anything
		// not known, then what is good. Two keys in sequence cannot express it, because a repository
		// whose build is good but whose tests fail must rank above one whose build is merely unknown.
		static int Tier(EstateTableRow row)
			=> row.Build == EstateHealth.Bad
				? 0
				: row.Test == EstateHealth.Bad
					? 1
					: row.Build == EstateHealth.Unknown || row.Test == EstateHealth.Unknown ? 2 : 3;

		var byName = StringComparer.OrdinalIgnoreCase;

		return sort switch
		{
			EstateSortColumn.Repository => rows.OrderBy(row => row.Name, byName),
			EstateSortColumn.Issues => rows.OrderByDescending(row => row.Issues).ThenBy(row => row.Name, byName),
			EstateSortColumn.Branch => rows.OrderBy(row => row.Branch ?? string.Empty, byName).ThenBy(row => row.Name, byName),
			EstateSortColumn.Uncommitted => rows.OrderBy(row => WorstFirst(row.Uncommitted)).ThenBy(row => row.Name, byName),
			EstateSortColumn.Unpushed => rows.OrderBy(row => WorstFirst(row.Unpushed)).ThenBy(row => row.Name, byName),
			EstateSortColumn.Sync => rows.OrderBy(row => WorstFirst(row.Sync)).ThenBy(row => row.Name, byName),
			EstateSortColumn.Build => rows.OrderBy(row => WorstFirst(row.Build)).ThenBy(row => row.Name, byName),
			EstateSortColumn.Test => rows.OrderBy(row => WorstFirst(row.Test)).ThenBy(row => row.Name, byName),
			EstateSortColumn.Category when category is { } chosen => rows
				.OrderByDescending(row => row.Cells.TryGetValue(chosen, out var cell) ? cell.Count : 0)
				.ThenBy(row => row.Name, byName),
			_ => rows
				.OrderBy(Tier)
				.ThenByDescending(row => row.Issues)
				.ThenBy(row => row.Name, byName)
		};
	}
}
