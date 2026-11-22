using System.Net;
using Octokit;
using Octokit.Internal;

namespace PanoramicData.NugetManagement.Test;

public class GitHubRateLimitMessageTests
{
	private sealed class FakeResponse : IResponse
	{
		public object Body => "rate limit";
		public IReadOnlyDictionary<string, string> Headers { get; } = new Dictionary<string, string>();
		public ApiInfo ApiInfo { get; } = new(new Dictionary<string, Uri>(), [], [], string.Empty, new RateLimit(5000, 0, 1893456000));
		public HttpStatusCode StatusCode => HttpStatusCode.Forbidden;
		public string ContentType => "application/json";
	}

	private static RateLimitExceededException CreateException() => new(new FakeResponse());

	[Fact]
	public async Task RunAsync_ReplacesARateLimitFailureWithAMessageNamingTheLimitAndHowToRetry()
	{
		var original = CreateException();

		var act = () => GitHubRateLimitMessage.RunAsync<int>(() => throw original);

		var thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
		thrown.Message.Should().Contain("rate limit").And.Contain("5000").And.Contain("2030-01-01").And.Contain("GitHub:Token");
		thrown.InnerException.Should().BeSameAs(original);
	}

	[Fact]
	public async Task RunAsync_ReturnsTheResultWhenNothingFails()
	{
		var result = await GitHubRateLimitMessage.RunAsync(() => Task.FromResult(42));

		result.Should().Be(42);
	}

	[Fact]
	public async Task RunAsync_DoesNotRewrapOtherFailures()
	{
		var act = () => GitHubRateLimitMessage.RunAsync<int>(() => throw new NotFoundException("missing", HttpStatusCode.NotFound));

		await act.Should().ThrowAsync<NotFoundException>();
	}
}
