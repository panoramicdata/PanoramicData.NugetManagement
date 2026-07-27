using Microsoft.Extensions.Configuration;
using Octokit;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Loads GitHub integration test settings from user secrets.
/// </summary>
internal static class GitHubIntegrationSettings
{
	private const string _defaultApiBaseUrl = "https://api.github.com";

	private sealed class SecretMarker;

	private static readonly Lazy<IConfigurationRoot> _configuration = new(() => new ConfigurationBuilder()
		.AddUserSecrets<SecretMarker>()
		.Build());

	public static string Token => _configuration.Value["GitHub:Token"]
		?? throw new InvalidOperationException("GitHub:Token was not found in user secrets for the test project.");

	/// <summary>
	/// Whether a GitHub token has been configured. Tests that talk to live GitHub are skipped
	/// rather than failed when it has not, so a missing developer secret is not reported as a
	/// broken build.
	/// </summary>
	public static bool IsConfigured
		=> !string.IsNullOrWhiteSpace(_configuration.Value["GitHub:Token"]);

	public static string ApiBaseUrl => _configuration.Value["GitHub:ApiBaseUrl"] ?? _defaultApiBaseUrl;

	public static GitHubClient CreateClient()
	{
		var client = new GitHubClient(new ProductHeaderValue("PanoramicData.NugetManagement.Tests"))
		{
			Credentials = new Credentials(Token)
		};
		return client;
	}
}
