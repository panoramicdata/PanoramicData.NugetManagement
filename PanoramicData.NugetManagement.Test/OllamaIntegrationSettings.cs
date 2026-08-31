using Microsoft.Extensions.Configuration;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Loads the Ollama settings the AI-fix integration tests need.
/// </summary>
/// <remarks>
/// The same shape as <see cref="GitHubIntegrationSettings"/>, for the same reason: these tests talk to
/// a real server, and a developer without one configured should see them skipped rather than see a red
/// build.
/// <para>
/// Set them in user secrets for the test project, or as environment variables so a build agent can
/// supply the same values:
/// <c>Ollama__BaseUrl</c>, <c>Ollama__Model</c>, and optionally <c>Ollama__ApiKey</c>.
/// </para>
/// </remarks>
internal static class OllamaIntegrationSettings
{
	private sealed class SecretMarker;

	private static readonly Lazy<IConfigurationRoot> _configuration = new(() => new ConfigurationBuilder()
		.AddUserSecrets<SecretMarker>()
		.AddEnvironmentVariables()
		.Build());

	/// <summary>The server's base address.</summary>
	public static string BaseUrl => _configuration.Value["Ollama:BaseUrl"]
		?? throw new InvalidOperationException("Ollama:BaseUrl was not found in user secrets or the environment.");

	/// <summary>The model to exercise.</summary>
	public static string Model => _configuration.Value["Ollama:Model"]
		?? throw new InvalidOperationException("Ollama:Model was not found in user secrets or the environment.");

	/// <summary>The optional API key; null for a local server that needs none.</summary>
	public static string? ApiKey => _configuration.Value["Ollama:ApiKey"];

	/// <summary>
	/// The context window to ask for, defaulting to what a 27b on a GX10 comfortably serves.
	/// </summary>
	public static int ContextWindow
		=> int.TryParse(_configuration.Value["Ollama:ContextWindow"], out var value) ? value : 131_072;

	/// <summary>
	/// Whether both a server and a model have been configured.
	/// </summary>
	public static bool IsConfigured
		=> !string.IsNullOrWhiteSpace(_configuration.Value["Ollama:BaseUrl"])
			&& !string.IsNullOrWhiteSpace(_configuration.Value["Ollama:Model"]);
}
