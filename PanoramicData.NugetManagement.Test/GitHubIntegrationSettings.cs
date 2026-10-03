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

	// User secrets for local development, environment variables so CI can supply the same settings
	// (there are no user secrets on a build agent). Environment variables are added last so they win:
	// set GitHub__Token in the environment to override, or to provide, the token.
	private static readonly Lazy<IConfigurationRoot> _configuration = new(() => new ConfigurationBuilder()
		.AddUserSecrets<SecretMarker>()
		.AddEnvironmentVariables()
		.Build());

	public static string Token => _configuration.Value["GitHub:Token"]
		?? throw new InvalidOperationException("GitHub:Token is not configured. Set it with: dotnet user-secrets set GitHub:Token <token> --project PanoramicData.NugetManagement.Test, or set the GitHub__Token environment variable.");

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
