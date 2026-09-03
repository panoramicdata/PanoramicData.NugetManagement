using Codacy.Api;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Asks Codacy where its analysis of a repository branch stands, right now.
/// </summary>
/// <remarks>
/// Separate from <see cref="ICodacyFileGradeService"/> because the caller is different and so is the
/// moment: the rules learn this once, when a repository is assessed, and the header chip needs it
/// when someone is actually looking at the repository — which may be hours later. Answering "how out
/// of date is this?" with a figure read at the last sweep would reproduce the defect being fixed.
/// </remarks>
public interface ICodacyAnalysisStateService
{
	/// <summary>
	/// Reads the current analysis state for a repository branch.
	/// </summary>
	/// <param name="apiToken">The Codacy API token.</param>
	/// <param name="organizationName">The GitHub organization name.</param>
	/// <param name="repositoryName">The repository name.</param>
	/// <param name="branch">The branch to ask about; may be null.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>
	/// The state, or null when Codacy could not be asked. Null means "not established" and never "no
	/// analysis is running", so callers must render it as silence rather than as reassurance.
	/// </returns>
	Task<CodacyAnalysisState?> GetStateAsync(
		string apiToken,
		string organizationName,
		string repositoryName,
		string? branch,
		CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="ICodacyAnalysisStateService"/> backed by the Codacy.Api client.
/// </summary>
public sealed class CodacyAnalysisStateService : ICodacyAnalysisStateService
{
	/// <inheritdoc />
	public async Task<CodacyAnalysisState?> GetStateAsync(
		string apiToken,
		string organizationName,
		string repositoryName,
		string? branch,
		CancellationToken cancellationToken)
	{
		using var client = new CodacyClient(new CodacyClientOptions { ApiToken = apiToken });

		return await CodacyAnalysisStateLookup.ResolveAsync(
			client,
			organizationName,
			repositoryName,
			branch,
			DateTimeOffset.UtcNow,
			cancellationToken).ConfigureAwait(false);
	}
}
