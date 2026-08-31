using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using PanoramicData.Blazor.Extensions;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Components;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Remediations;
using PanoramicData.NugetManagement.Web.Services;
using Refit;

var builder = WebApplication.CreateBuilder(args);

// Bind configuration
builder.Services.Configure<AppSettings>(builder.Configuration.GetSection("AppSettings"));

// Register PanoramicData.Blazor required services
builder.Services.AddPanoramicDataBlazor();

// Everything the app logs goes to stdout as before and, in addition, to the console panel in the UI.
// One ILogger, two destinations: work no longer has to narrate itself twice to be visible in both.
builder.Services.AddSingleton<UiConsoleLogSink>();
builder.Services.AddSingleton<ILoggerProvider>(sp =>
	new UiConsoleLoggerProvider(sp.GetRequiredService<UiConsoleLogSink>()));

// Register services
// A pooled handler rather than a client per package: discovery makes a hundred-odd nuspec requests
// in a burst, and a fresh HttpClient each time leaks a socket per package. Named rather than typed,
// because the resolver is consumed by a singleton and a typed client is registered transient.
builder.Services.AddHttpClient(NuspecRepositoryResolver.HttpClientName, client =>
{
	client.Timeout = TimeSpan.FromSeconds(15);
	client.DefaultRequestHeaders.UserAgent.ParseAdd("PanoramicData.NugetManagement");
});
builder.Services.AddSingleton<NuspecRepositoryResolver>();
builder.Services.AddSingleton<NuGetDiscoveryService>();
builder.Services.AddSingleton<IPublishedVersionSource, PublishedVersionService>();
builder.Services.AddSingleton<PublishedVersionRefresher>();
builder.Services.AddSingleton<LocalRepoService>();
builder.Services.AddSingleton<DashboardCacheService>();
builder.Services.AddSingleton<RemediationRegistry>();
// Singletons, because triage runs on many repository lanes at once and what stops one gap becoming
// one issue per repository is state shared between them.
builder.Services.AddSingleton(sp => new UncoveredDependencyIssueService(
	sp.GetRequiredService<IOptions<AppSettings>>().Value.GovernanceIssueRepository));
builder.Services.AddSingleton<DependabotTriageRunner>();
// Singletons: the model's server is one server however many repositories are being fixed, and the
// playbooks are the same for all of them.
builder.Services.AddSingleton<AiPlaybookRegistry>();
builder.Services.AddSingleton(sp => new OllamaGate(
	() => sp.GetRequiredService<RuntimeSettingsService>().Ollama.MaxConcurrency));
builder.Services.AddSingleton<RuntimeSettingsService>();
builder.Services.AddSingleton<IdeDetectionService>();
builder.Services.AddSingleton<LocalFileSystemDataProvider>();
builder.Services.AddSingleton<PackageDashboardDataProvider>();
builder.Services.AddSingleton<NavTreeDataProvider>();
// Work runs with no HTTP context, so the signed-in GitHub token has to be handed forward from a
// circuit rather than read from a request. See GitHubTokenProvider.
builder.Services.AddSingleton<GitHubTokenProvider>();
builder.Services.AddSingleton<RegressionGuardService>();
builder.Services.AddSingleton(_ => NuGetVersionCache.Default);
builder.Services.AddSingleton(_ => NuGetFloorCatalog.Default);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<NuGetVersionRefresher>(sp =>
{
	var checker = new NuGetVersionChecker(sp.GetRequiredService<ILogger<NuGetVersionChecker>>());
	return new NuGetVersionRefresher(
		NuGetVersionCache.Default,
		checker.GetLatestStableWithPublishedAsync,
		TimeProvider.System,
		sp.GetRequiredService<ILogger<NuGetVersionRefresher>>());
});
builder.Services.AddSingleton<NuGetVersionRefreshService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RegressionGuardService>());
// Nothing else resolves the refresher, so without this the cache never moves after seeding and every
// package the seed did not mention is a permanent cache miss.
builder.Services.AddHostedService(sp => sp.GetRequiredService<NuGetVersionRefreshService>());
builder.Services.AddScoped<DashboardService>();

// Per-repository work lanes: one queue-and-runner pair application-wide, replacing the single
// application-wide work queue that used to serialise every repository behind one another.
builder.Services.AddSingleton<WorkLaneService>(sp =>
{
	var runtimeSettings = sp.GetRequiredService<RuntimeSettingsService>();
	return new WorkLaneService(sp.GetRequiredService<ILogger<WorkLaneService>>())
	{
		MaxConcurrentLanes = runtimeSettings.MaxConcurrentLanes
	};
});
builder.Services.AddSingleton<WorkFanOut>();
builder.Services.AddSingleton(sp => new WorkQueueStore(
	WorkQueueStore.DefaultPath(),
	sp.GetRequiredService<ILogger<WorkQueueStore>>()));
// Scoped: WorkExecutors resolves DashboardService, which is itself scoped, so each running item needs
// its own scope rather than sharing the singleton runner's.
builder.Services.AddScoped<WorkExecutors>();
builder.Services.AddSingleton<WorkRunnerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkRunnerService>());

// GitHub OAuth authentication
var settings = builder.Configuration.GetSection("AppSettings").Get<AppSettings>() ?? new AppSettings();
var gitHubAuthConfigured = !string.IsNullOrEmpty(settings.GitHubClientId) && !string.IsNullOrEmpty(settings.GitHubClientSecret);

// Development-only: bypass GitHub OAuth with a synthetic local identity when explicitly enabled.
// Double-gated (Development environment AND AppSettings:DevAuthBypass) so Production always uses OAuth.
var devAuthBypass = builder.Environment.IsDevelopment() && settings.DevAuthBypass;

var authBuilder = builder.Services
	.AddAuthentication(options =>
	{
		if (devAuthBypass)
		{
			options.DefaultScheme = DevBypassAuthenticationHandler.SchemeName;
			options.DefaultAuthenticateScheme = DevBypassAuthenticationHandler.SchemeName;
			options.DefaultChallengeScheme = DevBypassAuthenticationHandler.SchemeName;
		}
		else
		{
			options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
			options.DefaultChallengeScheme = gitHubAuthConfigured ? "GitHub" : CookieAuthenticationDefaults.AuthenticationScheme;
		}
	})
	.AddCookie(options =>
	{
		options.LoginPath = "/login";
		options.LogoutPath = "/logout";
		options.ExpireTimeSpan = TimeSpan.FromDays(30);
		options.SlidingExpiration = true;
	});

if (devAuthBypass)
{
	authBuilder.AddScheme<AuthenticationSchemeOptions, DevBypassAuthenticationHandler>(
		DevBypassAuthenticationHandler.SchemeName, _ => { });
}

if (gitHubAuthConfigured)
{
	authBuilder.AddGitHub("GitHub", options =>
	{
		options.ClientId = settings.GitHubClientId;
		options.ClientSecret = settings.GitHubClientSecret;
		options.Scope.Add("repo");
		options.Scope.Add("read:org");
		options.SaveTokens = true;
	});
}

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();

// Add Blazor services
builder.Services.AddRazorComponents()
	.AddInteractiveServerComponents();

var app = builder.Build();

// The clone root is the app's own folder, so it exists from the start rather than appearing with the
// first clone. Without this, a freshly configured root is a path that is named everywhere and present
// nowhere — including on the settings page that offers to open it.
app.Services.GetRequiredService<LocalRepoService>().EnsureReposRootExists();

// Fetch the .NET channel Microsoft currently supports before serving, so assessments measure against
// the published standard rather than the offline fallback — and never against whatever SDKs happen to
// be installed on this machine, which is what this replaced. A failed fetch is not fatal.
using (var releaseIndexClient = new HttpClient { BaseAddress = DotNetReleaseCatalog.BaseAddress, Timeout = TimeSpan.FromSeconds(10) })
{
	await DotNetReleaseCatalog.Default.RefreshAsync(
		RestService.For<IDotNetReleaseIndexApi>(releaseIndexClient),
		CancellationToken.None).ConfigureAwait(false);
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
	app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Authentication endpoints
app.MapGet("/login", (HttpContext context) =>
{
	if (!gitHubAuthConfigured)
	{
		return Results.Text(
			"GitHub OAuth is not configured. Set GitHubClientId and GitHubClientSecret in user secrets. See secrets.example.json for details.",
			statusCode: 503);
	}

	return Results.Challenge(
		new AuthenticationProperties { RedirectUri = "/" },
		["GitHub"]);
});

app.MapGet("/logout", async (HttpContext context) =>
{
	await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
	return Results.Redirect("/");
});

app.MapGet("/api/user", (HttpContext context) =>
{
	if (context.User.Identity?.IsAuthenticated != true)
	{
		return Results.Json(new { authenticated = false });
	}

	return Results.Json(new
	{
		authenticated = true,
		name = context.User.FindFirstValue(ClaimTypes.Name),
		login = context.User.FindFirstValue("urn:github:login") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier),
		avatar = context.User.FindFirstValue("urn:github:avatar")
	});
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
	.AddInteractiveServerRenderMode();

app.Run();
