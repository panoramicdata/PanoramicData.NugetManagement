namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Decides when a change in the lanes justifies rebuilding the navigation tree.
/// </summary>
/// <remarks>
/// Rebuilding replaces the DOM subtree under every loaded branch, which is what makes the tree flicker
/// and the scrollbar jump. PDTree holds a snapshot of each branch from when it loaded, so anything that
/// changes <em>during</em> a run has to reach the screen by re-rendering the node template against live
/// state instead — which is how the repository spinners and health glyphs already work.
/// <para>
/// The distinction that matters is not "is work running" but "did the set of nodes change". Progress
/// reports arrive many times a second and change no node's existence: a running item's spinner and its
/// "repo 8 of 47" are template-driven. Queueing an item and finishing one do change it — a work node
/// appears or goes, and a finished run leaves different assessments, counts and findings behind. So the
/// rebuild follows the composition of the queue, and a burst of fifty progress reports on one item
/// costs nothing.
/// </para>
/// <para>
/// Not thread-safe, and does not need to be: it is only consulted from the debounce handler, which
/// marshals onto the renderer's own context.
/// </para>
/// </remarks>
public sealed class TreeReloadPolicy
{
	private HashSet<string>? _lastQueue;

	/// <summary>
	/// Records what is currently queued and says whether the tree should now be rebuilt.
	/// </summary>
	/// <param name="workItemIds">The ids of every outstanding work item, in any order.</param>
	/// <returns>True when the set differs from the last observation, including the first one.</returns>
	public bool ObserveAndShouldReload(IReadOnlyList<string> workItemIds)
	{
		var current = new HashSet<string>(workItemIds, StringComparer.Ordinal);

		// The first observation always rebuilds: whatever is queued at that point has never been drawn.
		if (_lastQueue is null)
		{
			_lastQueue = current;
			return true;
		}

		if (_lastQueue.SetEquals(current))
		{
			return false;
		}

		_lastQueue = current;
		return true;
	}
}
