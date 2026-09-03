using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// A <see cref="ICodacyAnalysisStateService"/> that knows nothing, for tests that construct a
/// <see cref="Web.Services.DashboardService"/> and are not about Codacy.
/// </summary>
/// <remarks>
/// Returns null, which is the same answer the real service gives when Codacy cannot be reached, so
/// nothing under test sees a state that could not occur in production.
/// </remarks>
internal sealed class NoopCodacyAnalysisStateService : ICodacyAnalysisStateService
{
	/// <inheritdoc />
	public Task<CodacyAnalysisState?> GetStateAsync(
		string apiToken,
		string organizationName,
		string repositoryName,
		string? branch,
		CancellationToken cancellationToken)
		=> Task.FromResult<CodacyAnalysisState?>(null);
}
