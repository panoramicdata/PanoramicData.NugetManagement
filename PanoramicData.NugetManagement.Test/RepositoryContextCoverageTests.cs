using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// How <see cref="RepositoryContextBuilder"/> decides what to put in
/// <c>RepositoryContext.LineCoveragePercent</c>.
/// </summary>
public class RepositoryContextCoverageTests
{
	private sealed class StubCoverageService(double? percent) : ICodacyCoverageService
	{
		public string? RequestedRepository { get; private set; }

		public Task<double?> GetLineCoveragePercentAsync(
			string apiToken,
			string organizationName,
			string repositoryName,
			string? branch,
			CancellationToken cancellationToken)
		{
			RequestedRepository = repositoryName;
			return Task.FromResult(percent);
		}
	}

	private sealed class ThrowingCoverageService : ICodacyCoverageService
	{
		public Task<double?> GetLineCoveragePercentAsync(
			string apiToken,
			string organizationName,
			string repositoryName,
			string? branch,
			CancellationToken cancellationToken)
			=> Task.FromException<double?>(new HttpRequestException("Codacy is down"));
	}

	[Fact]
	public async Task ResolveCoverageAsync_WithNoToken_DoesNotCallCodacy()
	{
		var stub = new StubCoverageService(80);

		var result = await RepositoryContextBuilder.ResolveCoverageAsync(
			stub,
			apiToken: null,
			organizationName: "panoramicdata",
			repositoryName: "Widget",
			branch: "main",
			CancellationToken.None);

		result.Should().BeNull();
		stub.RequestedRepository.Should().BeNull();
	}

	[Fact]
	public async Task ResolveCoverageAsync_WithToken_ReturnsTheCoverage()
	{
		var stub = new StubCoverageService(37.25);

		var result = await RepositoryContextBuilder.ResolveCoverageAsync(
			stub,
			apiToken: "a-token",
			organizationName: "panoramicdata",
			repositoryName: "Widget",
			branch: "main",
			CancellationToken.None);

		result.Should().Be(37.25);
		stub.RequestedRepository.Should().Be("Widget");
	}

	[Fact]
	public async Task ResolveCoverageAsync_WhenCodacyFails_ReturnsNullRatherThanFailingTheAssessment()
	{
		// A repository whose coverage could not be read is graded RED by TST-10, which is honest:
		// nothing was demonstrated. Aborting the whole assessment instead would lose every other
		// finding for that repository.
		var throwing = new ThrowingCoverageService();

		var result = await RepositoryContextBuilder.ResolveCoverageAsync(
			throwing,
			apiToken: "a-token",
			organizationName: "panoramicdata",
			repositoryName: "Widget",
			branch: "main",
			CancellationToken.None);

		result.Should().BeNull();
	}
}
