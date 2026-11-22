using Octokit;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Turns a GitHub rate limit failure in a live test into a message that says what to do about it.
/// </summary>
internal static class GitHubRateLimitMessage
{
	public static async Task<T> RunAsync<T>(Func<Task<T>> call)
	{
		try
		{
			return await call().ConfigureAwait(false);
		}
		catch (RateLimitExceededException ex)
		{
			throw new InvalidOperationException(Describe(ex), ex);
		}
	}

	public static string Describe(RateLimitExceededException exception)
		=> $"The GitHub API rate limit ({exception.Limit} requests per hour) is used up for the configured token. "
		+ $"It resets at {exception.Reset:u}. Re-run this test after that time, or set GitHub:Token to a token with a fresh quota.";
}
