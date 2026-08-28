namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// A request from a child component for the host to put work on the application-wide queue. The host
/// owns the queue, so a component that wants to run something describes it rather than running it.
/// </summary>
public sealed class QueuedWork
{
	/// <summary>What the user sees in the queue.</summary>
	public required string Title { get; init; }

	/// <summary>The organisation the work is scoped to, or null when it spans every organisation.</summary>
	public string? Organization { get; init; }

	/// <summary>Identifies a request that would repeat one already waiting in the queue.</summary>
	public required string DedupKey { get; init; }

	/// <summary>The work itself, reporting progress and honouring the queue's cancellation token.</summary>
	public required Func<IProgress<string>, CancellationToken, Task> Run { get; init; }
}
