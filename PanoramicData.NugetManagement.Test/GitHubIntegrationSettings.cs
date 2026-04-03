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

	public static string ApiBaseUrl => _configuration.Value["GitHub:ApiBaseUrl"] ?? _defaultApiBaseUrl;

	public static GitHubClient CreateClient()
	{
		var client = new GitHubClient(new ProductHeaderValue("PanoramicData.NugetManagement.Tests"));
		client.Credentials = new Credentials(Token);
		return client;
	}
}
