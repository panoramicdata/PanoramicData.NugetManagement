using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// The decision logic of <see cref="CodacyCoverageService"/>, exercised through an injectable reader
/// so it can be tested without a Codacy account — the same split
/// <c>CodacyAnalysisStateLookup</c> uses for the same reason.
/// </summary>
public class CodacyCoverageServiceTests
{
	[Fact]
	public async Task ReadCoverageAsync_WhenCodacyReportsAPercentage_ReturnsIt()
	{
		var result = await CodacyCoverageService.ReadCoverageAsync(
			_ => Task.FromResult<double?>(42.5),
			CancellationToken.None);

		result.Should().Be(42.5);
	}

	[Fact]
	public async Task ReadCoverageAsync_WhenCodacyHasNoCoverage_ReturnsNull()
	{
		var result = await CodacyCoverageService.ReadCoverageAsync(
			_ => Task.FromResult<double?>(null),
			CancellationToken.None);

		result.Should().BeNull();
	}

	[Fact]
	public async Task ReadCoverageAsync_WhenRepositoryIsNotOnCodacy_ReturnsNull()
	{
		// CodacyNotFound.Matches recognises a 404 in any of the shapes the Refit-generated client can
		// throw it in. HttpRequestException is the one that is trivially constructible in a test.
		var notFound = new HttpRequestException("not found", null, System.Net.HttpStatusCode.NotFound);

		var result = await CodacyCoverageService.ReadCoverageAsync(
			_ => Task.FromException<double?>(notFound),
			CancellationToken.None);

		result.Should().BeNull();
	}

	[Fact]
	public async Task ReadCoverageAsync_WhenCodacyIsUnreachable_Propagates()
	{
		// The whole point of the 404-only rule. An unreachable Codacy reported as "no coverage" would
		// grade every repository RED on a network blip, and the AI fixer acts on RED.
		var unreachable = new HttpRequestException("connection refused");

		var act = async () => await CodacyCoverageService.ReadCoverageAsync(
			_ => Task.FromException<double?>(unreachable),
			CancellationToken.None).ConfigureAwait(false);

		await act.Should().ThrowAsync<HttpRequestException>();
	}
}
