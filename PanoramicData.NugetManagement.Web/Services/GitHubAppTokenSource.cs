using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Web.Models;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Mints and caches the installation token of the GitHub App the work runner acts as, so background
/// work spends the App's own rate limit and not the signed-in user's.
/// </summary>
/// <remarks>
/// GitHub applies the 5,000-an-hour REST limit per user, so every token a person owns shares one quota,
/// and one organisation-wide refresh can spend all of it and stop everything else that person runs.
/// An App installation token has a quota of its own.
/// <para>
/// Fails closed. Once an App id is configured, anything wrong (no key, an unreadable key, GitHub refusing
/// the exchange, no or several installations) throws. It never falls back to the user's token, because
/// that would put the load back on the shared quota without anyone noticing.
/// </para>
/// <para>
/// Neither the private key, the signed JWT nor the installation token is logged or placed in an
/// exception message.
/// </para>
/// </remarks>
public sealed class GitHubAppTokenSource(
	IOptions<AppSettings> settings,
	IHttpClientFactory httpClientFactory,
	TimeProvider timeProvider,
	ILogger<GitHubAppTokenSource> logger)
{
	/// <summary>The name of the pooled HTTP client used to talk to the GitHub API.</summary>
	public const string HttpClientName = "GitHubApp";

	private const string _apiRoot = "https://api.github.com/";

	// GitHub rejects a JWT that expires more than ten minutes ahead, and asks for the issued-at time to
	// be backdated to allow for clock skew.
	private static readonly TimeSpan _jwtLifetime = TimeSpan.FromMinutes(9);
	private static readonly TimeSpan _jwtBackdate = TimeSpan.FromSeconds(60);

	// Installation tokens last an hour; renewing early means no request is ever sent with one about to lapse.
	private static readonly TimeSpan _renewBefore = TimeSpan.FromMinutes(5);

	private readonly SemaphoreSlim _gate = new(1, 1);
	private volatile CachedToken? _cached;
	private long? _discoveredInstallationId;

	private sealed record CachedToken(string Token, DateTimeOffset RenewAt);

	/// <summary>
	/// True when an App id is set. Deliberately true on a partial configuration (an id with no key), so
	/// that a half-configured App fails loudly instead of quietly using the user's token.
	/// </summary>
	public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.Value.GitHubAppId);

	/// <summary>
	/// The current installation token, minted or renewed as needed. Safe to call from many runner threads:
	/// only one of them mints.
	/// </summary>
	/// <param name="cancellationToken">Cancels a mint in progress.</param>
	/// <returns>A token that is valid for at least five more minutes.</returns>
	/// <exception cref="InvalidOperationException">No App is configured, or the App cannot be used.</exception>
	public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
	{
		if (!IsConfigured)
		{
			throw new InvalidOperationException("No GitHub App is configured: AppSettings:GitHubAppId is empty.");
		}

		if (_cached is { } fresh && timeProvider.GetUtcNow() < fresh.RenewAt)
		{
			return fresh.Token;
		}

		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_cached is { } again && timeProvider.GetUtcNow() < again.RenewAt)
			{
				return again.Token;
			}

			var minted = await MintAsync(cancellationToken).ConfigureAwait(false);
			_cached = minted;
			return minted.Token;
		}
		finally
		{
			_gate.Release();
		}
	}

	private async Task<CachedToken> MintAsync(CancellationToken cancellationToken)
	{
		var options = settings.Value;
		var now = timeProvider.GetUtcNow();
		var jwt = CreateJwt(options.GitHubAppId!, LoadPrivateKey(options), now);

		using var client = httpClientFactory.CreateClient(HttpClientName);

		var installationId = options.GitHubAppInstallationId ?? _discoveredInstallationId;
		if (installationId is null)
		{
			installationId = await DiscoverInstallationAsync(client, jwt, options.GitHubOrganization, cancellationToken)
				.ConfigureAwait(false);
			_discoveredInstallationId = installationId;
		}

		using var request = NewRequest(HttpMethod.Post, $"app/installations/{installationId}/access_tokens", jwt);
		using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException(
				$"GitHub refused the installation token request for installation {installationId}: "
				+ $"{(int)response.StatusCode} {response.ReasonPhrase}. Check the App id, the private key, "
				+ "and that the App is still installed on the organisation.");
		}

		using var json = await JsonDocument.ParseAsync(
			await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
			cancellationToken: cancellationToken).ConfigureAwait(false);

		if (!json.RootElement.TryGetProperty("token", out var tokenElement)
			|| tokenElement.GetString() is not { Length: > 0 } token
			|| !json.RootElement.TryGetProperty("expires_at", out var expiresElement)
			|| !DateTimeOffset.TryParse(
				expiresElement.GetString(),
				CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal,
				out var expiresAt))
		{
			throw new InvalidOperationException(
				$"GitHub's installation token response for installation {installationId} had no usable token or expiry.");
		}

		var renewAt = expiresAt - _renewBefore;
		if (renewAt <= now)
		{
			renewAt = now.AddMinutes(1);
		}

		logger.LogInformation(
			"GitHub App installation token minted for installation {InstallationId}; renews at {RenewAt:O}.",
			installationId,
			renewAt);
		return new CachedToken(token, renewAt);
	}

	private static async Task<long> DiscoverInstallationAsync(
		HttpClient client,
		string jwt,
		string organisation,
		CancellationToken cancellationToken)
	{
		using var request = NewRequest(HttpMethod.Get, "app/installations?per_page=100", jwt);
		using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException(
				$"GitHub refused the request to list the App's installations: {(int)response.StatusCode} {response.ReasonPhrase}. "
				+ "Check the App id and the private key, or set AppSettings:GitHubAppInstallationId.");
		}

		using var json = await JsonDocument.ParseAsync(
			await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
			cancellationToken: cancellationToken).ConfigureAwait(false);

		var installations = json.RootElement.EnumerateArray()
			.Select(element => (
				Id: element.GetProperty("id").GetInt64(),
				Account: element.TryGetProperty("account", out var account) && account.TryGetProperty("login", out var login)
					? login.GetString() ?? string.Empty
					: string.Empty))
			.ToList();

		var candidates = string.IsNullOrWhiteSpace(organisation)
			? installations
			: installations.Where(i => string.Equals(i.Account, organisation, StringComparison.OrdinalIgnoreCase)).ToList();

		if (candidates.Count == 1)
		{
			return candidates[0].Id;
		}

		throw new InvalidOperationException(candidates.Count == 0
			? $"The GitHub App has no installation on '{organisation}' ({installations.Count} installation(s) in total). "
				+ "Install it on the organisation, or set AppSettings:GitHubAppInstallationId."
			: $"The GitHub App has {candidates.Count} installations to choose from. "
				+ "Set AppSettings:GitHubAppInstallationId to pick one.");
	}

	private static HttpRequestMessage NewRequest(HttpMethod method, string path, string jwt)
	{
		var request = new HttpRequestMessage(method, new Uri(new Uri(_apiRoot), path));
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
		request.Headers.Accept.ParseAdd("application/vnd.github+json");
		request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
		return request;
	}

	private static string LoadPrivateKey(AppSettings options)
	{
		var pem = options.GitHubAppPrivateKey;
		if (string.IsNullOrWhiteSpace(pem) && !string.IsNullOrWhiteSpace(options.GitHubAppPrivateKeyFile))
		{
			pem = File.ReadAllText(options.GitHubAppPrivateKeyFile);
		}

		if (string.IsNullOrWhiteSpace(pem))
		{
			throw new InvalidOperationException(
				"AppSettings:GitHubAppId is set but there is no private key. "
				+ "Set AppSettings:GitHubAppPrivateKey or AppSettings:GitHubAppPrivateKeyFile.");
		}

		// An environment variable cannot always carry real newlines, so accept the escaped form too.
		return pem.Replace("\\n", "\n", StringComparison.Ordinal).Trim();
	}

	private static string CreateJwt(string appId, string privateKeyPem, DateTimeOffset now)
	{
		using var rsa = RSA.Create();
		try
		{
			rsa.ImportFromPem(privateKeyPem);
		}
		catch (Exception ex) when (ex is ArgumentException or CryptographicException)
		{
			// The inner exception is left out on purpose: nothing derived from the key goes in a message.
			throw new InvalidOperationException("The GitHub App private key is not a valid PEM-encoded RSA key.");
		}

		var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
		var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
		{
			iat = (now - _jwtBackdate).ToUnixTimeSeconds(),
			exp = (now + _jwtLifetime).ToUnixTimeSeconds(),
			iss = appId
		}));
		var signature = rsa.SignData(
			Encoding.ASCII.GetBytes($"{header}.{payload}"),
			HashAlgorithmName.SHA256,
			RSASignaturePadding.Pkcs1);
		return $"{header}.{payload}.{Base64Url(signature)}";
	}

	private static string Base64Url(byte[] bytes)
		=> Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
