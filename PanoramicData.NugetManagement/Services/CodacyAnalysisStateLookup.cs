using Codacy.Api;
using Codacy.Api.Models;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Asks Codacy where its analysis of a repository branch had got to.
/// </summary>
/// <remarks>
/// Two endpoints, because neither alone answers the question — see
/// <see cref="CodacyAnalysisStateMapper"/>. Both are best-effort: the caller already has its grades
/// by the time this runs, and losing them because a second call failed would be a worse answer than
/// reporting them without a freshness caveat.
/// </remarks>
internal static class CodacyAnalysisStateLookup
{
	/// <summary>
	/// Reads the analysis state for a branch, or returns null when Codacy tells us nothing.
	/// </summary>
	/// <param name="client">A Codacy client.</param>
	/// <param name="organizationName">The GitHub organization name.</param>
	/// <param name="repositoryName">The repository name.</param>
	/// <param name="branch">The branch to ask about; may be null.</param>
	/// <param name="now">The moment of asking.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	public static Task<CodacyAnalysisState?> ResolveAsync(
		CodacyClient client,
		string organizationName,
		string repositoryName,
		string? branch,
		DateTimeOffset now,
		CancellationToken cancellationToken)
		=> ResolveAsync(
			async token => (await client.Analysis
				.GetFirstAnalysisOverviewAsync(Provider.Github, organizationName, repositoryName, branch, token)
				.ConfigureAwait(false)).Data,
			async token => (await client.Analysis
				.GetRepositoryWithAnalysisAsync(Provider.Github, organizationName, repositoryName, branch, token)
				.ConfigureAwait(false)).Data.LastAnalysedCommit,
			now,
			cancellationToken);

	/// <summary>
	/// The same resolution over two supplied readers, so the failure handling can be tested without a
	/// Codacy account.
	/// </summary>
	/// <returns>
	/// The state, or null when neither reader answered. Null means "not established" and never "no
	/// analysis is running": the rules render it as silence rather than as reassurance.
	/// </returns>
	/// <remarks>
	/// The two are read independently and either may fail on its own. Half an answer still tells the
	/// reader an analysis is in flight, which is the fact that matters most, so requiring both would
	/// throw the useful half away. Cancellation is the one exception that propagates — a cancelled
	/// assessment has to stop rather than report an unknown freshness and carry on.
	/// </remarks>
	public static async Task<CodacyAnalysisState?> ResolveAsync(
		Func<CancellationToken, Task<FirstAnalysisOverview?>> getOverviewAsync,
		Func<CancellationToken, Task<Commit?>> getLastAnalysedCommitAsync,
		DateTimeOffset now,
		CancellationToken cancellationToken)
	{
		var overview = await TryAsync(getOverviewAsync, cancellationToken).ConfigureAwait(false);
		var commit = await TryAsync(getLastAnalysedCommitAsync, cancellationToken).ConfigureAwait(false);

		return overview is null && commit is null
			? null
			: CodacyAnalysisStateMapper.From(overview, commit, now);
	}

	/// <summary>
	/// Runs one reader, treating any failure other than cancellation as "no answer".
	/// </summary>
	private static async Task<T?> TryAsync<T>(
		Func<CancellationToken, Task<T?>> readAsync,
		CancellationToken cancellationToken) where T : class
	{
		try
		{
			return await readAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return null;
		}
	}
}
