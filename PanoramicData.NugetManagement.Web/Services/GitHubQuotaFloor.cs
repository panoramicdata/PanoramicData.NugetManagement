namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Decides whether a refresh should stop spending GitHub quota (#163).
/// </summary>
public static class GitHubQuotaFloor
{
	/// <summary>The remaining calls below which background fan-out is not started.</summary>
	public static int Floor { get; } = 1500;

	/// <summary>
	/// A message explaining the pause, or null when the refresh may continue or the quota is unknown.
	/// </summary>
	/// <param name="remaining">Calls left in the current window, from the last response headers.</param>
	/// <param name="reset">When the window resets.</param>
	public static string? PauseMessage(int? remaining, DateTimeOffset? reset)
		=> remaining is null || remaining >= Floor
			? null
			: $"GitHub quota is down to {remaining} calls (floor {Floor}); not queuing further work"
				+ (reset is null ? "." : $" until it resets at {reset.Value.UtcDateTime:HH:mm} UTC.");
}
