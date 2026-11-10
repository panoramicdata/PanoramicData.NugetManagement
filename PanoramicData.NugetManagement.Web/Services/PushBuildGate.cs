namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Refuses a push when the repository does not build with the change about to be pushed.
/// </summary>
/// <remarks>
/// A newer xunit.v3 was pushed to main in several repositories by a push that nothing built first, and
/// their release runs failed on analyzer errors the new version enforces. The bulk apply loop already built
/// before pushing; the single-repository Commit and push did not, so the rule lives where every push goes.
/// </remarks>
internal static class PushBuildGate
{
	private const int MaxErrorLines = 3;

	/// <summary>
	/// Builds, and returns why the push must not happen, or null when it may.
	/// </summary>
	/// <param name="repository">The repository's full name, for the message.</param>
	/// <param name="build">Builds the repository.</param>
	/// <param name="onOutput">Where progress is reported.</param>
	public static async Task<string?> RefusalAsync(
		string? repository,
		Func<Task<(bool Success, string Output)>> build,
		Action<string>? onOutput = null)
	{
		ArgumentNullException.ThrowIfNull(build);

		onOutput?.Invoke($"🔎 Verifying {repository} builds before pushing...");

		var (success, output) = await build().ConfigureAwait(false);
		if (success)
		{
			return null;
		}

		var errors = (output ?? string.Empty)
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Where(line => line.Contains(" error ", StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.Ordinal)
			.Take(MaxErrorLines)
			.ToList();

		var reason = $"Not pushed: {repository} does not build with these changes, so pushing would break its main branch."
			+ (errors.Count > 0 ? " First errors: " + string.Join(" | ", errors) : " The build output has the details.");
		onOutput?.Invoke($"⛔ {reason}");
		return reason;
	}
}
