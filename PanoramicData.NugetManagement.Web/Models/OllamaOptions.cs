namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// Where the local model is, which one to use, and how hard to work it.
/// </summary>
/// <remarks>
/// Runtime-editable rather than in <c>appsettings.json</c>, because changing model is something you do
/// while trying things rather than at deployment.
/// <para>
/// <see cref="ApiKey"/> is stored in the runtime settings file in plain text, beside settings that are
/// not secret. That was a deliberate choice — the common case is a local box that needs no key at all —
/// but anyone who can read the file can read the key, and the settings UI says so.
/// </para>
/// </remarks>
public sealed class OllamaOptions
{
	/// <summary>
	/// The server's base address, for example <c>http://pdl-rune-02.panoramicdata.com:11434</c>.
	/// </summary>
	public string? BaseUrl { get; set; }

	/// <summary>
	/// An optional API key. Left empty for a local instance, which needs none.
	/// </summary>
	public string? ApiKey { get; set; }

	/// <summary>
	/// The model to use, for example <c>qwen3.8:27b</c>.
	/// </summary>
	/// <remarks>
	/// No default. Every server has a different set pulled, so a guess would fail with "model not
	/// found" and look like a broken feature rather than an unset field.
	/// </remarks>
	public string? Model { get; set; }

	/// <summary>
	/// The context window to ask for, in tokens.
	/// </summary>
	/// <remarks>
	/// Stated explicitly because Ollama's own default is commonly far smaller than the model supports,
	/// and a silently truncated conversation looks exactly like a model that has forgotten the task.
	/// </remarks>
	public int ContextWindow { get; set; } = 131_072;

	/// <summary>How long a single call may take, in milliseconds.</summary>
	public int RequestTimeoutMs { get; set; } = 300_000;

	/// <summary>
	/// How many AI fixes may run at once.
	/// </summary>
	/// <remarks>
	/// One by default. A single box serving one model does not benefit from twenty concurrent sessions,
	/// and this is the setting that stops an estate-wide sweep from flattening it.
	/// </remarks>
	public int MaxConcurrency { get; set; } = 1;

	/// <summary>Tool-calling turns allowed within one attempt.</summary>
	public int MaxTurnsPerAttempt { get; set; } = 12;

	/// <summary>Attempts allowed on one rule before giving up.</summary>
	public int MaxAttemptsPerRule { get; set; } = 3;

	/// <summary>
	/// Whether there is enough here to attempt an AI fix at all.
	/// </summary>
	/// <remarks>
	/// Both a usable address and a model are required. Offering the action without them would produce a
	/// queued item that fails on its first call, which reads as the feature being broken rather than
	/// unconfigured — so the UI hides it until this is true.
	/// </remarks>
	public bool IsConfigured
		=> !string.IsNullOrWhiteSpace(Model)
			&& Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
			&& (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

	/// <summary>
	/// A copy with every numeric field brought into a workable range.
	/// </summary>
	/// <remarks>
	/// Clamped on the way in rather than on every read, so what is persisted is what will be used —
	/// a stored zero concurrency would queue AI work that never ran.
	/// </remarks>
	public OllamaOptions Normalised() => new()
	{
		BaseUrl = BaseUrl?.Trim(),
		ApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim(),
		Model = Model?.Trim(),
		ContextWindow = Math.Max(2_048, ContextWindow),
		RequestTimeoutMs = Math.Max(5_000, RequestTimeoutMs),
		MaxConcurrency = Math.Max(1, MaxConcurrency),
		MaxTurnsPerAttempt = Math.Max(1, MaxTurnsPerAttempt),
		MaxAttemptsPerRule = Math.Max(1, MaxAttemptsPerRule)
	};
}
