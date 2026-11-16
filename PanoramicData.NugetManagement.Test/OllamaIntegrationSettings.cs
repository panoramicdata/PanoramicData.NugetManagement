using Microsoft.Extensions.Configuration;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Loads the Ollama settings the AI-fix integration tests need.
/// </summary>
/// <remarks>
/// The same shape as <see cref="GitHubIntegrationSettings"/>: these tests talk to a real server and fail
/// with a message naming the missing setting when it is not configured.
/// <para>
/// Set them in user secrets for the test project, or as environment variables so a build agent can
/// supply the same values:
/// <c>Ollama__BaseUrl</c>, <c>Ollama__Model</c>, and optionally <c>Ollama__ApiKey</c>.
/// </para>
/// </remarks>
internal static class OllamaIntegrationSettings
{
	private sealed class SecretMarker;

	/// <summary>
	/// The test category every class that needs a real model carries, as
	/// <c>[Trait("Category", OllamaIntegrationSettings.Category)]</c>.
	/// </summary>
	/// <remarks>
	/// The manual Ollama integration workflow runs <c>Category=Ollama</c> against pdl-rune-02. These classes
	/// also carry Category=Integration, which CI excludes.
	/// </remarks>
	public const string Category = "Ollama";

	private static readonly Lazy<IConfigurationRoot> _configuration = new(() => new ConfigurationBuilder()
		.AddUserSecrets<SecretMarker>()
		.AddEnvironmentVariables()
		.Build());

	/// <summary>The server every model test runs against unless <c>Ollama:BaseUrl</c> says otherwise.</summary>
	public const string DefaultBaseUrl = "http://pdl-rune-02.panoramicdata.com:11434";

	/// <summary>The models the AI fix is supported on, in the order the tests run them.</summary>
	public static readonly string[] SupportedModels =
	[
		"qwen3.8:27b",
		"nemotron-3.5-super:120b-a12b-q4_K_M"
	];

	/// <summary>The server's base address.</summary>
	public static string BaseUrl => _configuration.Value["Ollama:BaseUrl"] ?? DefaultBaseUrl;

	/// <summary>The single model to exercise where a test is not run per supported model.</summary>
	public static string Model => _configuration.Value["Ollama:Model"] ?? SupportedModels[0];

	/// <summary>The models a per-model test runs: every supported one, plus <c>Ollama:Model</c> when it names a candidate not yet on the list.</summary>
	public static string[] Models => _configuration.Value["Ollama:Model"] is { Length: > 0 } extra && !SupportedModels.Contains(extra)
		? [.. SupportedModels, extra]
		: SupportedModels;

	/// <summary>The optional API key; null for a local server that needs none.</summary>
	public static string? ApiKey => _configuration.Value["Ollama:ApiKey"];

	/// <summary>
	/// The context window to ask for, defaulting to what a 27b on a GX10 comfortably serves.
	/// </summary>
	public static int ContextWindow
		=> int.TryParse(_configuration.Value["Ollama:ContextWindow"], out var value) ? value : 131_072;
}
