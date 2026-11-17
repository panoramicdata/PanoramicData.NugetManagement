using PanoramicData.Blazor;
using PanoramicData.Blazor.Models;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

public partial class NavTreeDataProvider
{
	/// <summary>
	/// Adds a lane's "Work" container and one node per outstanding item, or nothing when there is
	/// nothing to show under it. An empty container would be a node to open and find nothing in.
	/// </summary>
	/// <remarks>
	/// An organisation's node is shown when its own lane has items <em>or any repository beneath it
	/// does</em>, which is not the same rule as a repository's. It has to be: the node's header
	/// carries the "stop everything below here" button, which is what a fanned-out bulk action is
	/// stopped with now that it is many items rather than one. A discovery item runs on the
	/// organisation lane, fans out into forty repository lanes and then completes — so with the
	/// narrower rule the button would disappear at the exact moment forty lanes started needing it.
	/// <para>
	/// Its children are still only the organisation lane's own items. The descendants belong to their
	/// own repositories' work nodes, which is why the count says where the work actually is rather
	/// than implying it is all here.
	/// </para>
	/// </remarks>
	private void AddWorkNodes(
		List<NavItem> items,
		string workKey,
		string parentKey,
		string laneKey,
		string organization,
		string? repositoryFullName,
		int sortOrder)
	{
		var laneItems = _workLanes?.ItemsFor(laneKey) ?? [];

		// Only the organisation node rolls up. A repository is the bottom of the hierarchy: there is
		// nothing beneath it to count.
		var itemsBelow = repositoryFullName is null
			? CountWorkItemsUnderOrganisation(organization, laneKey)
			: 0;

		// A repository's node is permanent; an organisation's is not. The repository is where a reader
		// asks "is anything happening here?", and a node that comes and goes answers that only to
		// someone who already knows the node comes and goes. An organisation has no such fixed place —
		// it exists to carry the stop-everything-below button, so it appears when there is something to
		// stop.
		if (laneItems.Count == 0 && itemsBelow == 0 && repositoryFullName is null)
		{
			return;
		}

		items.Add(WorkContainerNode(workKey, parentKey, laneKey, organization, repositoryFullName, sortOrder, laneItems.Count, itemsBelow));

		AddOwnWorkNodes(items, laneItems, workKey, laneKey, organization, repositoryFullName);

		if (itemsBelow > 0)
		{
			AddMirroredWorkNodes(items, workKey, laneKey, organization, laneItems.Count);
		}
	}

	private static void AddOwnWorkNodes(
		List<NavItem> items,
		IReadOnlyList<Models.WorkItem> laneItems,
		string workKey,
		string laneKey,
		string organization,
		string? repositoryFullName)
	{
		for (var index = 0; index < laneItems.Count; index++)
		{
			var workItem = laneItems[index];

			items.Add(new NavItem
			{
				Key = WorkItemKey(workItem.Id),
				Text = workItem.Title,
				ParentKey = workKey,
				IconCss = WorkItemIcon(workItem.State),
				View = NavView.WorkItemDetail,
				Organization = organization,
				RepositoryFullName = repositoryFullName,
				LaneKey = laneKey,
				WorkItemId = workItem.Id,
				WorkItemState = workItem.State,
				WorkItemProgress = workItem.Progress,
				IsLeaf = true,
				// The lane's own order, not alphabetical: the queue's order is the information.
				SortOrder = index
			});
		}
	}

	private static NavItem WorkContainerNode(
		string workKey,
		string parentKey,
		string laneKey,
		string organization,
		string? repositoryFullName,
		int sortOrder,
		int ownCount,
		int itemsBelow)
	{
		// Green for a clear lane, blue for a lane with anything in it — queued counts, because work
		// waiting to start is still work this repository is going to do.
		var isEmpty = ownCount == 0 && itemsBelow == 0;
		var workStatus = isEmpty ? PackageHealthStatus.Success : PackageHealthStatus.Info;

		return new NavItem
		{
			Key = workKey,
			Text = WorkNodeText(ownCount, itemsBelow),
			ParentKey = parentKey,
			IconCss = NavHealthRollup.Icon("fas fa-list-check", workStatus),
			View = NavView.WorkQueue,
			Organization = organization,
			RepositoryFullName = repositoryFullName,
			LaneKey = laneKey,
			// Expandable whenever it covers anything at all, its descendants' work included, so a
			// "Work (3 below)" node can be opened to see those three.
			IsLeaf = isEmpty,
			SortOrder = sortOrder
		};
	}

	private void AddMirroredWorkNodes(List<NavItem> items, string workKey, string laneKey, string organization, int ownCount)
	{
		// The work in the repositories' own lanes, mirrored here. Listed rather than merely counted so
		// that the node reporting it can be opened to see it, and so the stop-all button on this node
		// never reaches anything invisible.
		var below = WorkItemsUnderOrganisation(organization, laneKey);

		for (var index = 0; index < below.Count; index++)
		{
			var (itemRepositoryFullName, workItem) = below[index];
			var shortName = ShortRepositoryName(itemRepositoryFullName);

			items.Add(new NavItem
			{
				Key = OrgWorkItemKey(workItem.Id),
				Text = MirroredWorkText(shortName, workItem.Title),
				ParentKey = workKey,
				IconCss = WorkItemIcon(workItem.State),
				View = NavView.WorkItemDetail,
				Organization = organization,
				RepositoryFullName = itemRepositoryFullName,
				LaneKey = RepositoryLaneKey(itemRepositoryFullName),
				WorkItemId = workItem.Id,
				WorkItemState = workItem.State,
				WorkItemProgress = workItem.Progress,
				IsLeaf = true,
				// After this node's own items, keeping "here" above "below" as the label describes them.
				SortOrder = ownCount + index
			});
		}
	}

	private static string WorkItemIcon(Models.WorkItemState state) => state switch
	{
		Models.WorkItemState.Running => "fas fa-circle-notch fa-spin",
		Models.WorkItemState.Cancelling => "fas fa-rotate-left fa-spin",
		_ => "fas fa-clock"
	};

	/// <summary>
	/// The outstanding work in an organisation's repositories' lanes, with the repository each item
	/// belongs to.
	/// </summary>
	/// <param name="organization">The organisation whose descendants to gather.</param>
	/// <param name="ownLaneKey">The organisation's own lane, which is listed separately.</param>
	private List<(string RepositoryFullName, Models.WorkItem Item)> WorkItemsUnderOrganisation(
		string organization,
		string ownLaneKey)
		=> _workLanes is null
			? []
			: [.. _workLanes.Lanes
				.Where(lane => !string.Equals(lane.Key, ownLaneKey, StringComparison.Ordinal)
					&& string.Equals(lane.Organization, organization, StringComparison.OrdinalIgnoreCase))
				.OrderBy(lane => lane.Key, StringComparer.OrdinalIgnoreCase)
				.SelectMany(lane => lane.Items
					.Select(item => (item.RepositoryFullName ?? lane.Key, item)))

				// Stable, so within each group the order is still by repository and then the lane's own.
				.OrderBy(entry => ActivityRank(entry.item))];

	/// <summary>
	/// Where an item belongs in a list of everything outstanding: what is working first, then what is
	/// waiting.
	/// </summary>
	/// <param name="item">The work item.</param>
	/// <remarks>
	/// Ordered by repository name, a list of thirty items put the one doing something wherever its
	/// repository happened to fall, and finding it meant scanning for the spinner. Of the running items
	/// the one that has reported progress comes first: every lane's head is "running", but only one
	/// holds the model, and an AI session reports a status line only once it does. The others are
	/// running in the sense of owning a lane while they wait their turn.
	/// </remarks>
	internal static int ActivityRank(Models.WorkItem item) => item.State switch
	{
		Models.WorkItemState.Running or Models.WorkItemState.Cancelling when item.Progress is { Length: > 0 } => 0,
		Models.WorkItemState.Running or Models.WorkItemState.Cancelling => 1,
		_ => 2
	};

	/// <summary>
	/// A mirrored item's label: the repository, then what the work is.
	/// </summary>
	/// <remarks>
	/// The same title appears under every repository — twelve "Re-assess" items are indistinguishable
	/// without it. The repository is left off where the title already names it, which most of them do,
	/// rather than reading "Athonet.Api — Re-assess Athonet.Api".
	/// </remarks>
	private static string MirroredWorkText(string repositoryShortName, string title)
		=> title.Contains(repositoryShortName, StringComparison.OrdinalIgnoreCase)
			? title
			: $"{repositoryShortName} — {title}";

	/// <summary>The repository's name without its owner.</summary>
	private static string ShortRepositoryName(string repositoryFullName)
		=> repositoryFullName.Contains('/', StringComparison.Ordinal)
			? repositoryFullName.Split('/')[^1]
			: repositoryFullName;

	/// <summary>
	/// One repository's lane key, as <see cref="WorkDescriptor.LaneKey"/> builds it.
	/// </summary>
	/// <param name="repositoryFullName">The repository, as <c>owner/name</c>.</param>
	public static string RepositoryLaneKey(string repositoryFullName)
		=> $"repo:{repositoryFullName.ToLowerInvariant()}";

	/// <summary>
	/// Whether a repository is actually working, as opposed to merely having work queued.
	/// </summary>
	/// <param name="laneKey">The repository's lane.</param>
	private bool IsRepositoryLaneRunning(string laneKey)
		=> (_workLanes?.ItemsFor(laneKey) ?? [])
			.Any(i => i.State is Models.WorkItemState.Running or Models.WorkItemState.Cancelling);

	/// <summary>
	/// How much outstanding work sits in the lanes belonging to an organisation, other than in its own
	/// lane — that is, in its repositories'.
	/// </summary>
	/// <param name="organization">The organisation whose descendants to count.</param>
	/// <param name="ownLaneKey">The organisation's own lane, which is counted separately.</param>
	private int CountWorkItemsUnderOrganisation(string organization, string ownLaneKey)
		=> _workLanes is null
			? 0
			: _workLanes.Lanes
				.Where(lane => !string.Equals(lane.Key, ownLaneKey, StringComparison.Ordinal)
					&& string.Equals(lane.Organization, organization, StringComparison.OrdinalIgnoreCase))
				.Sum(lane => lane.Items.Count);

	/// <summary>
	/// The work node's label, saying where the work it covers actually is.
	/// </summary>
	/// <remarks>
	/// A bare total would claim that forty repository items are in this lane, and expanding the node
	/// to find one discovery item under it would then read as a bug. "Below" is where the stop-all
	/// button reaches, which is the whole reason the node is shown at all.
	/// </remarks>
	/// <param name="ownCount">Items in this node's own lane.</param>
	/// <param name="itemsBelow">Items in lanes beneath it, which it does not list.</param>
	private static string WorkNodeText(int ownCount, int itemsBelow) => (ownCount, itemsBelow) switch
	{
		// Only a repository's permanent node reaches this: an organisation's is not shown when both
		// counts are zero. "Work (0)" would be a number to read and discard on every repository.
		(0, 0) => "Work",
		(0, var below) => $"Work ({below} below)",
		(var own, 0) => $"Work ({own})",
		var (own, below) => $"Work ({own} here, {below} below)"
	};
}
