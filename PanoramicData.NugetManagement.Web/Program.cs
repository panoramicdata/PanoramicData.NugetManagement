using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using PanoramicData.NugetManagement.Web.Components;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Bind configuration
builder.Services.Configure<AppSettings>(builder.Configuration.GetSection("AppSettings"));

// Register services
builder.Services.AddSingleton<NuGetDiscoveryService>();
builder.Services.AddSingleton<LocalRepoService>();
builder.Services.AddScoped<DashboardService>();

// GitHub OAuth authentication
var settings = builder.Configuration.GetSection("AppSettings").Get<AppSettings>() ?? new AppSettings();

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = "GitHub";
    })
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
    })
    .AddGitHub("GitHub", options =>
    {
        options.ClientId = settings.GitHubClientId;
        options.ClientSecret = settings.GitHubClientSecret;
        options.Scope.Add("repo");
        options.Scope.Add("read:org");
        options.SaveTokens = true;
    });

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();

// Add Blazor services
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

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
