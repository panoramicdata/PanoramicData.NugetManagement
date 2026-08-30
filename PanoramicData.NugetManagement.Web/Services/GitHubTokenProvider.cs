namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Holds the most recently seen signed-in GitHub access token, so that work running with no HTTP
/// context can still authenticate.
/// </summary>
/// <remarks>
/// This exists because work no longer belongs to the browser tab that asked for it.
/// <see cref="WorkRunnerService"/> pumps the lanes from the host's own asynchronous flow, not from a
/// request, so <see cref="IHttpContextAccessor.HttpContext"/> is <c>null</c> on a runner thread —
/// always, not merely sometimes. Reading the token from the request there yields nothing, and the
/// resulting client is anonymous: private repositories cannot be assessed at all, and public ones
/// share the 60-per-hour anonymous rate limit, which one fanned-out organisation re-assessment
/// exhausts on its first pass.
/// <para>
/// The token is therefore handed forward from a circuit, which does have a request to read it from,
/// and picked up here by whatever runs later. One token application-wide is deliberate: the
/// application is single-user by design — the estate, the local clone root and the queue are all
/// one person's — so "the signed-in user" is not ambiguous.
/// </para>
/// <para>
/// Thread-safe: written by circuits as they open and read by up to <c>MaxConcurrentLanes</c> runner
/// threads at once.
/// </para>
/// </remarks>
public sealed class GitHubTokenProvider
{
	private volatile string? _accessToken;

	/// <summary>
	/// The most recently published access token, or null when no circuit has published one since the
	/// application started.
	/// </summary>
	public string? AccessToken => _accessToken;

	/// <summary>
	/// Publishes the token a circuit read from its own request, for work that runs later without one.
	/// </summary>
	/// <param name="accessToken">
	/// The token, or null to leave the last known one in place — a circuit that opened without a token
	/// knows nothing about the user's sign-in, and must not blank out a token that still works.
	/// </param>
	public void Publish(string? accessToken)
	{
		if (!string.IsNullOrWhiteSpace(accessToken))
		{
			_accessToken = accessToken;
		}
	}
}
