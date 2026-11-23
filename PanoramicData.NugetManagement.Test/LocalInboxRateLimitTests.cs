using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Octokit;
using Octokit.Internal;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// A rate limit while reading the inbox must not turn a local assessment into a failure.
/// </summary>
public sealed class LocalInboxRateLimitTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("nugetmgmt-inbox-limit-").FullName;

	private static GitHubClient ClientAnswering(HttpStatusCode status, string message, bool exhausted)
	{
		var handler = new StubHttpMessageHandler(() =>
		{
			var response = new HttpResponseMessage(status)
			{
				Content = new StringContent($"{{\"message\":\"{message}\"}}", System.Text.Encoding.UTF8, "application/json")
			};
			if (exhausted)
			{
				response.Headers.Add("X-RateLimit-Limit", "60");
				response.Headers.Add("X-RateLimit-Remaining", "0");
				response.Headers.Add("X-RateLimit-Reset", "4102444800");
			}

			return response;
		});

		var connection = new Connection(
			new ProductHeaderValue("nm-test"),
			new Uri("https://api.github.com"),
			new InMemoryCredentialStore(Credentials.Anonymous),
			new HttpClientAdapter(() => handler),
			new SimpleJsonSerializer());

		return new GitHubClient(connection);
	}

	[Fact]
	public async Task RateLimitedInboxFetch_ReturnsWarningInsteadOfThrowing()
	{
		var row = new RepositoryDashboardRow
		{
			RepositoryFullName = "panoramicdata/Sample",
			OpenIssuesKnown = true
		};

		var warning = await CreateDashboardService().RefreshInboxIfPossibleAsync(
			row,
			ClientAnswering(HttpStatusCode.Forbidden, "API rate limit exceeded", exhausted: true),
			TestContext.Current.CancellationToken);

		warning.Should().Contain("rate limit");
		row.OpenIssuesKnown.Should().BeFalse("a stale inbox must not be drawn as a known-good one");
	}

	private DashboardService CreateDashboardService()
	{
		var appSettings = Options.Create(new AppSettings { LocalReposRoot = _root });
		var runtimeSettings = new RuntimeSettingsService(appSettings, NullLogger<RuntimeSettingsService>.Instance);
		var localRepo = new LocalRepoService(runtimeSettings, NullLogger<LocalRepoService>.Instance);
		var cache = new DashboardCacheService(
			NullLogger<DashboardCacheService>.Instance,
			Path.Combine(_root, "dashboard-cache.json"));

		return DashboardTestFactory.Create(appSettings, runtimeSettings, localRepo, cache);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// A locked temp file is not worth failing the test that produced it.
		}

		GC.SuppressFinalize(this);
	}
}
