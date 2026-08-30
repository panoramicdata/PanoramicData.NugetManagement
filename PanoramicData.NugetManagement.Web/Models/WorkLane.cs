namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// One queue of work: everything outstanding for a single repository, or for a single organisation.
/// </summary>
/// <remarks>
/// The lane is the unit of serialisation. One item runs per lane at a time — the invariant that
/// stops two tabs, or a fix and a build, driving the same working tree at once — while different
/// lanes run concurrently, because different repositories share nothing.
/// </remarks>
public sealed class WorkLane
{
	/// <summary>The lane's key, as built by <see cref="WorkDescriptor.LaneKey"/>.</summary>
	public required string Key { get; init; }

	/// <summary>The repository this lane belongs to, or null for an organisation lane.</summary>
	public string? RepositoryFullName { get; init; }

	/// <summary>The organisation this lane belongs to.</summary>
	public string? Organization { get; init; }

	/// <summary>Outstanding work, running item first. Finished items are removed.</summary>
	public List<WorkItem> Items { get; } = [];

	/// <summary>Whether the scheduler has promoted this lane and an item is executing on it.</summary>
	public bool IsRunning { get; set; }
}
