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
/// What the template cannot do is change the set of nodes. A finished run can produce an entirely
/// different one: repositories appear, assessments turn into different failing rules, closed Dependabot
/// pull requests stop being findings. So the tree is rebuilt exactly once, on the transition from
/// "something is running" to "nothing is" — one rebuild per run instead of one every 250 milliseconds
/// for its duration.
/// </para>
/// <para>
/// Not thread-safe, and does not need to be: it is only ever consulted from the debounce handler, which
/// marshals onto the renderer's own context.
/// </para>
/// </remarks>
public sealed class TreeReloadPolicy
{
	private bool _wasRunning;

	/// <summary>
	/// Records the current state of the lanes and says whether the tree should now be rebuilt.
	/// </summary>
	/// <param name="anyRunning">Whether any lane is currently running work.</param>
	/// <returns>True exactly once per run, as the last lane finishes.</returns>
	public bool ObserveAndShouldReload(bool anyRunning)
	{
		var justWentIdle = _wasRunning && !anyRunning;
		_wasRunning = anyRunning;

		return justWentIdle;
	}
}
