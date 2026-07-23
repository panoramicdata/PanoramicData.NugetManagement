using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Development-only authentication handler that auto-authenticates every request with a
/// synthetic local user, bypassing the GitHub OAuth sign-in flow entirely.
/// <para>
/// This is wired up in <c>Program.cs</c> only when the app is running in the
/// <c>Development</c> environment AND <c>AppSettings:DevAuthBypass</c> is <c>true</c>.
/// It is never registered in Production, so a real deployment always uses GitHub OAuth.
/// </para>
/// <para>
/// No credentials are involved: the identity is fabricated locally. If a GitHub PAT is
/// configured (<c>AppSettings:GitHubPat</c>) it is surfaced as the <c>access_token</c> so
/// that <c>HttpContext.GetTokenAsync("access_token")</c> can drive GitHub API calls for
/// assessing un-cloned repositories. Without a PAT, discovery (nuget.org) and assessment
/// of locally-cloned repositories still work.
/// </para>
/// </summary>
public sealed class DevBypassAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
	/// <summary>
	/// The name of the development bypass authentication scheme.
	/// </summary>
	public const string SchemeName = "DevBypass";

	private readonly AppSettings _appSettings;

	/// <summary>
	/// Initializes a new instance of the <see cref="DevBypassAuthenticationHandler"/> class.
	/// </summary>
	public DevBypassAuthenticationHandler(
		IOptionsMonitor<AuthenticationSchemeOptions> options,
		ILoggerFactory logger,
		UrlEncoder encoder,
		IOptions<AppSettings> appSettings)
		: base(options, logger, encoder)
	{
		_appSettings = appSettings.Value;
	}

	/// <inheritdoc />
	protected override Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		var userName = string.IsNullOrWhiteSpace(_appSettings.DevAuthUser)
			? "dev"
			: _appSettings.DevAuthUser;

		var claims = new[]
		{
			new Claim(ClaimTypes.Name, userName),
			new Claim(ClaimTypes.NameIdentifier, userName),
			new Claim("urn:github:login", userName)
		};

		var identity = new ClaimsIdentity(claims, SchemeName);
		var principal = new ClaimsPrincipal(identity);
		var properties = new AuthenticationProperties();

		// Surface a GitHub token (if configured) so GetTokenAsync("access_token") works for API calls.
		if (!string.IsNullOrWhiteSpace(_appSettings.GitHubPat))
		{
			properties.StoreTokens([
				new AuthenticationToken { Name = "access_token", Value = _appSettings.GitHubPat }
			]);
		}

		var ticket = new AuthenticationTicket(principal, properties, SchemeName);
		return Task.FromResult(AuthenticateResult.Success(ticket));
	}
}
