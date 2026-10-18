using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for acting as a GitHub App. The point of the App is a rate limit of its own, so what matters is
/// that the exchange is correct, that one token serves many runner threads, and that a broken App is an
/// error rather than a silent return to the user's token.
/// </summary>
public class GitHubAppTokenSourceTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _appId = "5369352";
	private const long _installationId = 172720077;
	private static readonly DateTimeOffset _start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
	private static readonly (string Private, string Public) _keys = NewKeys();

	[Fact]
	public async Task NoAppIdShouldMeanNotConfigured()
	{
		var (source, handler, _) = Create(new AppSettings());

		source.IsConfigured.Should().BeFalse();
		var act = () => source.GetTokenAsync(TestContext.Current.CancellationToken);
		await act.Should().ThrowAsync<InvalidOperationException>();
		handler.Requests.Should().BeEmpty();
	}

	[Fact]
	public async Task ShouldExchangeASignedJwtForAnInstallationToken()
	{
		var (source, handler, _) = Create(Settings(), Token("ghs_first", _start.AddHours(1)));

		var token = await source.GetTokenAsync(TestContext.Current.CancellationToken);

		token.Should().Be("ghs_first");
		var request = handler.Requests.Should().ContainSingle().Subject;
		request.Method.Should().Be(HttpMethod.Post);
		request.Url.Should().Be($"https://api.github.com/app/installations/{_installationId}/access_tokens");
		request.Accept.Should().Contain("application/vnd.github+json");

		var jwt = BearerOf(request);
		var parts = jwt.Split('.');
		parts.Should().HaveCount(3);
		Json(parts[0]).GetProperty("alg").GetString().Should().Be("RS256");
		var payload = Json(parts[1]);
		payload.GetProperty("iss").GetString().Should().Be(_appId);
		payload.GetProperty("exp").GetInt64().Should().BeLessThanOrEqualTo(_start.AddMinutes(10).ToUnixTimeSeconds());
		payload.GetProperty("iat").GetInt64().Should().BeLessThan(_start.ToUnixTimeSeconds());

		using var publicKey = RSA.Create();
		publicKey.ImportFromPem(_keys.Public);
		publicKey.VerifyData(
			Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
			Decode(parts[2]),
			HashAlgorithmName.SHA256,
			RSASignaturePadding.Pkcs1).Should().BeTrue();
	}

	[Fact]
	public async Task ShouldReuseTheTokenUntilFiveMinutesBeforeItExpires()
	{
		var (source, handler, time) = Create(
			Settings(),
			Token("ghs_first", _start.AddHours(1)),
			Token("ghs_second", _start.AddHours(2)));
		var ct = TestContext.Current.CancellationToken;

		(await source.GetTokenAsync(ct)).Should().Be("ghs_first");

		time.Advance(TimeSpan.FromMinutes(54));
		(await source.GetTokenAsync(ct)).Should().Be("ghs_first");
		handler.Requests.Should().HaveCount(1);

		time.Advance(TimeSpan.FromMinutes(2));
		(await source.GetTokenAsync(ct)).Should().Be("ghs_second");
		handler.Requests.Should().HaveCount(2);
	}

	[Fact]
	public async Task ShouldMintOnlyOnceWhenManyRunnerThreadsAskAtOnce()
	{
		var (source, handler, _) = Create(Settings(), Token("ghs_first", _start.AddHours(1), delayMilliseconds: 100));
		var ct = TestContext.Current.CancellationToken;

		var tokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => source.GetTokenAsync(ct), ct)));

		tokens.Should().OnlyContain(t => t == "ghs_first");
		handler.Requests.Should().HaveCount(1);
	}

	[Fact]
	public async Task ShouldFindTheSingleInstallationWhenNoneIsConfigured()
	{
		var (source, handler, _) = Create(
			Settings(installation: null),
			Json(HttpStatusCode.OK, """[{"id":42,"account":{"login":"panoramicdata"}}]"""),
			Token("ghs_first", _start.AddHours(1)));

		(await source.GetTokenAsync(TestContext.Current.CancellationToken)).Should().Be("ghs_first");

		handler.Requests[0].Method.Should().Be(HttpMethod.Get);
		handler.Requests[0].Url.Should().Be("https://api.github.com/app/installations?per_page=100&page=1");
		handler.Requests[1].Url.Should().Be("https://api.github.com/app/installations/42/access_tokens");
	}

	[Fact]
	public async Task ShouldReadEveryPageOfInstallationsBeforeChoosing()
	{
		var firstPage = "[" + string.Join(",", Enumerable.Range(1, 100).Select(i => $$$"""{"id":{{{i}}},"account":{"login":"other-{{{i}}}"}}""")) + "]";
		var (source, handler, _) = Create(
			Settings(installation: null),
			Json(HttpStatusCode.OK, firstPage),
			Json(HttpStatusCode.OK, """[{"id":4242,"account":{"login":"panoramicdata"}}]"""),
			Token("ghs_first", _start.AddHours(1)));

		(await source.GetTokenAsync(TestContext.Current.CancellationToken)).Should().Be("ghs_first");

		handler.Requests.Should().HaveCount(3);
		handler.Requests[0].Url.Should().Be("https://api.github.com/app/installations?per_page=100&page=1");
		handler.Requests[1].Url.Should().Be("https://api.github.com/app/installations?per_page=100&page=2");
		handler.Requests[2].Url.Should().Be("https://api.github.com/app/installations/4242/access_tokens");
	}

	[Fact]
	public async Task ShouldPickTheInstallationOnTheOrganisationWhenThereAreSeveral()
	{
		var (source, handler, _) = Create(
			Settings(installation: null, organisation: "PanoramicData"),
			Json(HttpStatusCode.OK, """[{"id":7,"account":{"login":"someone-else"}},{"id":42,"account":{"login":"panoramicdata"}}]"""),
			Token("ghs_first", _start.AddHours(1)));

		await source.GetTokenAsync(TestContext.Current.CancellationToken);

		handler.Requests[1].Url.Should().Be("https://api.github.com/app/installations/42/access_tokens");
	}

	[Fact]
	public async Task ShouldRefuseToGuessBetweenInstallations()
	{
		var (source, _, _) = Create(
			Settings(installation: null, organisation: string.Empty),
			Json(HttpStatusCode.OK, """[{"id":7,"account":{"login":"a"}},{"id":42,"account":{"login":"b"}}]"""));

		var act = () => source.GetTokenAsync(TestContext.Current.CancellationToken);

		(await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("GitHubAppInstallationId");
	}

	[Fact]
	public async Task ShouldSayWhenTheAppIsNotInstalledOnTheOrganisation()
	{
		var (source, _, _) = Create(
			Settings(installation: null),
			Json(HttpStatusCode.OK, """[{"id":7,"account":{"login":"someone-else"}}]"""));

		var act = () => source.GetTokenAsync(TestContext.Current.CancellationToken);

		(await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("no installation on 'panoramicdata'");
	}

	[Fact]
	public async Task ShouldFailWithoutLeakingAnythingWhenGitHubRefusesTheExchange()
	{
		var (source, handler, _) = Create(Settings(), Json(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}"""));

		var act = () => source.GetTokenAsync(TestContext.Current.CancellationToken);

		var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
		message.Should().Contain("401");
		message.Should().NotContain("Bearer");
		message.Should().NotContain(BearerOf(handler.Requests.Single()));
		AssertNoKeyMaterial(message);
	}

	[Fact]
	public async Task ShouldFailClearlyWhenThereIsNoPrivateKey()
	{
		var settings = Settings();
		settings.GitHubAppPrivateKey = null;
		var (source, handler, _) = Create(settings);

		var act = () => source.GetTokenAsync(TestContext.Current.CancellationToken);

		(await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("private key");
		handler.Requests.Should().BeEmpty();
	}

	[Fact]
	public async Task ShouldRejectAnInvalidKeyWithoutQuotingIt()
	{
		var (source, handler, _) = Create(Settings(key: "-----BEGIN RSA PRIVATE KEY-----\nthis is not a key\n-----END RSA PRIVATE KEY-----"));

		var act = () => source.GetTokenAsync(TestContext.Current.CancellationToken);

		var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
		message.Should().Contain("not a valid PEM");
		message.Should().NotContain("this is not a key");
		handler.Requests.Should().BeEmpty();
	}

	[Fact]
	public async Task ShouldAcceptAKeyWhoseNewlinesAreEscaped()
	{
		var (source, _, _) = Create(Settings(key: _keys.Private.Replace("\r", string.Empty).Replace("\n", "\\n")), Token("ghs_first", _start.AddHours(1)));

		(await source.GetTokenAsync(TestContext.Current.CancellationToken)).Should().Be("ghs_first");
	}

	[Fact]
	public async Task ShouldReadTheKeyFromAFile()
	{
		var path = Path.Combine(AppContext.BaseDirectory, $"github-app-{Guid.NewGuid():N}.pem");
		await File.WriteAllTextAsync(path, _keys.Private, TestContext.Current.CancellationToken);
		try
		{
			var settings = Settings();
			settings.GitHubAppPrivateKey = null;
			settings.GitHubAppPrivateKeyFile = path;
			var (source, _, _) = Create(settings, Token("ghs_first", _start.AddHours(1)));

			(await source.GetTokenAsync(TestContext.Current.CancellationToken)).Should().Be("ghs_first");
		}
		finally
		{
			File.Delete(path);
		}
	}

	private static (GitHubAppTokenSource Source, RecordingHandler Handler, FakeTimeProvider Time) Create(
		AppSettings settings,
		params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
	{
		var handler = new RecordingHandler(responses);
		var time = new FakeTimeProvider(_start);
		var source = new GitHubAppTokenSource(
			Options.Create(settings),
			new StubHttpClientFactory(handler),
			time,
			NullLogger<GitHubAppTokenSource>.Instance);
		return (source, handler, time);
	}

	internal static AppSettings Settings(string? key = null, long? installation = _installationId, string organisation = "panoramicdata")
		=> new()
		{
			GitHubAppId = _appId,
			GitHubAppInstallationId = installation,
			GitHubAppPrivateKey = key ?? _keys.Private,
			GitHubOrganization = organisation
		};

	internal static Func<HttpRequestMessage, HttpResponseMessage> Token(string token, DateTimeOffset expires, int delayMilliseconds = 0)
		=> _ =>
		{
			if (delayMilliseconds > 0)
			{
				Thread.Sleep(delayMilliseconds);
			}

			var expiry = expires.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
			return Response(HttpStatusCode.Created, $$"""{"token":"{{token}}","expires_at":"{{expiry}}"}""");
		};

	internal static Func<HttpRequestMessage, HttpResponseMessage> Json(HttpStatusCode status, string body)
		=> _ => Response(status, body);

	private static HttpResponseMessage Response(HttpStatusCode status, string body)
		=> new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	private static string BearerOf(RecordedRequest request)
		=> request.Authorization!["Bearer ".Length..];

	private static JsonElement Json(string base64Url)
		=> JsonDocument.Parse(Decode(base64Url)).RootElement;

	private static byte[] Decode(string base64Url)
	{
		var padded = base64Url.Replace('-', '+').Replace('_', '/');
		return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
	}

	private static void AssertNoKeyMaterial(string text)
	{
		text.Should().NotContain("PRIVATE KEY");
		foreach (var line in _keys.Private.Split('\n').Where(l => l.Length > 20 && !l.StartsWith("-----", StringComparison.Ordinal)))
		{
			text.Should().NotContain(line.Trim());
		}
	}

	private static (string Private, string Public) NewKeys()
	{
		using var rsa = RSA.Create(2048);
		return (rsa.ExportRSAPrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
	}
}

/// <summary>
/// Tests for which token background work carries. The guarantee that matters is the last one: with an
/// App configured but broken, the user's token must not be quietly used instead.
/// </summary>
public class GitHubTokenProviderAppTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public async Task WithoutAnAppTheWorkTokenShouldBeTheUsersToken()
	{
		var provider = new GitHubTokenProvider();
		(await provider.GetWorkTokenAsync(TestContext.Current.CancellationToken)).Should().BeNull();

		provider.Publish("user-token");

		provider.UsesGitHubApp.Should().BeFalse();
		(await provider.GetWorkTokenAsync(TestContext.Current.CancellationToken)).Should().Be("user-token");
	}

	[Fact]
	public async Task WithAnAppTheWorkTokenShouldBeTheInstallationTokenAndNotTheUsers()
	{
		var provider = new GitHubTokenProvider(NewSource(GitHubAppTokenSourceTests.Settings(), GitHubAppTokenSourceTests.Token("ghs_first", DateTimeOffset.UtcNow.AddHours(1))));
		provider.Publish("user-token");

		provider.UsesGitHubApp.Should().BeTrue();
		(await provider.GetWorkTokenAsync(TestContext.Current.CancellationToken)).Should().Be("ghs_first");
		provider.AccessToken.Should().Be("user-token");
	}

	[Fact]
	public async Task WithABrokenAppTheProviderShouldThrowAndNotUseTheUsersToken()
	{
		var settings = GitHubAppTokenSourceTests.Settings();
		settings.GitHubAppPrivateKey = null;
		var provider = new GitHubTokenProvider(NewSource(settings));
		provider.Publish("user-token");

		provider.UsesGitHubApp.Should().BeTrue();
		var act = () => provider.GetWorkTokenAsync(TestContext.Current.CancellationToken);
		await act.Should().ThrowAsync<InvalidOperationException>();
	}

	private static GitHubAppTokenSource NewSource(AppSettings settings, params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
		=> new(
			Options.Create(settings),
			new StubHttpClientFactory(new RecordingHandler(responses)),
			TimeProvider.System,
			NullLogger<GitHubAppTokenSource>.Instance);
}

internal sealed record RecordedRequest(HttpMethod Method, string Url, string? Authorization, string Accept);

/// <summary>
/// Answers each request from a script and remembers it, so a test can check what was sent and in what
/// order. The last scripted response repeats once the script is exhausted.
/// </summary>
internal sealed class RecordingHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
{
	private readonly List<RecordedRequest> _requests = [];

	public IReadOnlyList<RecordedRequest> Requests
	{
		get
		{
			lock (_requests)
			{
				return [.. _requests];
			}
		}
	}

	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		int index;
		lock (_requests)
		{
			index = Math.Min(_requests.Count, responses.Length - 1);
			_requests.Add(new RecordedRequest(
				request.Method,
				request.RequestUri!.ToString(),
				request.Headers.Authorization?.ToString(),
				request.Headers.Accept.ToString()));
		}

		return Task.FromResult(responses[index](request));
	}
}
