# PanoramicData.NugetManagement

[![Codacy Badge](https://app.codacy.com/project/badge/Grade/PLACEHOLDER)](https://app.codacy.com/gh/panoramicdata/PanoramicData.NugetManagement/dashboard)
[![NuGet](https://img.shields.io/nuget/v/PanoramicData.NugetManagement)](https://www.nuget.org/packages/PanoramicData.NugetManagement)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

Opinionated assessment of NuGet packages in a GitHub organization for best practices compliance.

## Overview

PanoramicData.NugetManagement connects to a GitHub organization, examines each repository, and evaluates it against a comprehensive set of opinionated best practice rules covering:

- **CI/CD** — CI workflow structure, checkout depth, action versions
- **Versioning** — Nerdbank.GitVersioning, global.json SDK pinning
- **Central Package Management** — CPM enabled, no inline versions
- **NuGet Hygiene** — snupkg symbols, GeneratePackageOnBuild, PackageReadmeFile, no deprecated dependencies, deprecated packages' repositories archived
- **Target Framework** — Latest .NET version
- **Build Quality** — TreatWarningsAsErrors, Nullable, ImplicitUsings
- **Code Quality** — .editorconfig, file-scoped namespaces, Codacy, CodeQL
- **Testing** — Test project existence, xUnit v3, Microsoft.Testing.Platform test runner, code coverage
- **Serialization** — System.Text.Json preferred over Newtonsoft
- **HTTP Clients** — Refit preferred
- **Licensing** — MIT LICENSE, PackageLicenseExpression, Copyright
- **README & Badges** — Codacy, NuGet, License badges
- **Repository Hygiene** — .gitignore, NeutralResourcesLanguage
- **Project Metadata** — PackageId, RepositoryUrl, Authors, PackageIcon
- **Community Health** — SECURITY.md, CONTRIBUTING.md
- **Dependency Automation** — Dependabot or Renovate

## Installation

```shell
dotnet add package PanoramicData.NugetManagement
```

## Quick Start

```csharp
using Octokit;
using Microsoft.Extensions.Logging;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

// Create an authenticated GitHub client
var github = new GitHubClient(new ProductHeaderValue("MyApp"))
{
    Credentials = new Credentials("your-github-token")
};

// Configure assessment options
var options = new AssessmentOptions
{
    OrganizationName = "your-org",
    RepositoryOptions = new Dictionary<string, RepoOptions>
    {
        ["legacy-repo"] = new() { Exclude = true },
        ["web-app"] = new() { IsPackable = false }
    }
};

// Run the assessment
using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var assessor = new OrganizationAssessor(
    github,
    loggerFactory.CreateLogger<OrganizationAssessor>(),
    loggerFactory.CreateLogger<RepositoryContextBuilder>());

var result = await assessor.AssessAsync(options);

// Report results
Console.WriteLine($"Organization: {result.OrganizationName}");
Console.WriteLine($"Repositories: {result.RepositoryCount}");
Console.WriteLine($"Compliant: {result.CompliantCount}");
Console.WriteLine($"Non-compliant: {result.NonCompliantCount}");

foreach (var repo in result.RepositoryAssessments)
{
    Console.WriteLine($"\n{repo.RepositoryFullName}: {repo.PassedCount}/{repo.RuleResults.Count} passed");
    foreach (var failure in repo.RuleResults.Where(r => !r.Passed))
    {
        Console.WriteLine($"  [{failure.Severity}] {failure.RuleId}: {failure.Message}");
        if (failure.Remediation is not null)
        {
            Console.WriteLine($"    Fix: {failure.Remediation}");
        }
    }
}
```

## Running the web dashboard locally

The repository also contains `PanoramicData.NugetManagement.Web`, a Blazor dashboard for
assessing and remediating an organisation's repositories. It needs configuration before it will
start usefully: `appsettings.json` ships with empty values, and nothing below has a default that
works out of the box.

Settings are read from configuration, so supply them however you prefer. For local development
use [user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) — they live
outside the repository, so they cannot be committed by accident. All commands below are run from
the repository root:

```shell
dotnet user-secrets set "AppSettings:GitHubOrganization" "your-github-org" --project PanoramicData.NugetManagement.Web
dotnet user-secrets set "AppSettings:NuGetOrganization" "your-nuget-org" --project PanoramicData.NugetManagement.Web
```

### Signing in

The dashboard authenticates with GitHub OAuth, so it needs an OAuth app. Register one at
**Settings → Developer settings → OAuth Apps** on GitHub. The default launch profile serves
`http://localhost:5023`, so the callback URL is `http://localhost:5023/signin-github`. Sign-in
requests the `repo` and `read:org` scopes. Then:

```shell
dotnet user-secrets set "AppSettings:GitHubClientId" "..." --project PanoramicData.NugetManagement.Web
dotnet user-secrets set "AppSettings:GitHubClientSecret" "..." --project PanoramicData.NugetManagement.Web
```

If you would rather not register an OAuth app, development can bypass sign-in entirely. This is
double-gated — it only takes effect when the environment is `Development` **and** the setting is
on, so it can never apply in production:

```shell
dotnet user-secrets set "AppSettings:DevAuthBypass" "true" --project PanoramicData.NugetManagement.Web
# optional, defaults to "dev"
dotnet user-secrets set "AppSettings:DevAuthUser" "your-github-username" --project PanoramicData.NugetManagement.Web
```

### Recommended settings

| Setting | Why you want it |
|---|---|
| `AppSettings:GitHubPat` | A personal access token covering the same ground as the OAuth scopes above (`repo`, `read:org`). Without one, assessing more than a handful of repositories exhausts GitHub's unauthenticated rate limit. When `DevAuthBypass` is on, this is also used as the access token, so the dashboard can reach GitHub without signing in. |
| `AppSettings:LocalReposRoot` | The folder your repositories are cloned into. Required before the dashboard can clone, build, test, auto-fix or publish anything — without it only read-only assessment works. This one can also be set from the dashboard's own settings page, which overrides the configured value. |
| `AppSettings:CodacyApiToken` | Enables the Codacy rules: is the repository set up and analysed (CQ-03), its open issues (CQ-05), and its per-file grades (CQ-06, informational). Without it those rules report as not applicable. |

Then run it:

```shell
dotnet run --project PanoramicData.NugetManagement.Web
```

### Running the tests

Most tests need nothing, but the live GitHub integration tests need a token. Without one they skip
with an explanatory message — and because this project sets `failSkips: true` (skipped tests are
treated as failures, so nothing can hide behind a `Skip`), **a token is required for a green
suite**:

```shell
dotnet user-secrets set "GitHub:Token" (Read-Host) --project PanoramicData.NugetManagement.Test
```

That is PowerShell: typing the value at the `Read-Host` prompt keeps it out of your shell history,
which is worth doing since history files are stored in plain text. In other shells, pass the token
as the final argument instead.

Alternatively set the `GitHub__Token` environment variable, which takes precedence over user
secrets. That is how to supply it in CI, since build agents have no user secrets.

#### These tests spend your GitHub API budget

The integration tests call the live GitHub API, so they draw on the same hourly rate limit
(5,000 requests for an authenticated user) as everything else that token does. Two consequences are
worth knowing before they catch you out:

- **Reusing one token for the dashboard and the tests makes them compete.** The limit is per token,
  not per application, so assessing the organisation in the dashboard and running the suite eat from
  the same allowance. Repeated full runs of both in one hour can exhaust it. Giving the tests their
  own token keeps the two budgets separate.
- **Exhausting it looks like broken tests, not a spent budget.** The failure surfaces as
  `Octokit.RateLimitExceededException`, and because `failSkips: true` treats skips as failures, there
  is no gentle degradation — the suite simply goes red. If integration tests fail while everything
  else passes, check the exception before hunting for a regression: it resolves itself when the
  window resets, on the hour from your first request.

The dashboard shows the remaining budget in its breadcrumb bar and warns before an action that would
consume a large share of it, but that reflects the token the application uses — the test token's
budget is separate and not shown there.

For scale: assessing one repository costs about two API requests. File contents are fetched from
`raw.githubusercontent.com`, which is a CDN and does not count against the limit, and repositories
already cloned locally are assessed from disk at no API cost at all.

## Per-Repository Options

```csharp
var repoOptions = new RepoOptions
{
    Exclude = false,                     // Set true to skip entirely
    IsPackable = true,                   // Set false for apps/tools (skips NuGet rules)
    EnforceRequiredProperties = true,    // Configurable 'required' keyword enforcement
    SuppressedRules = ["HTTP-01"]        // Suppress specific rules by ID
};
```

## Available Rules

Use `RuleRegistry.Rules` to enumerate all discovered rules at runtime.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

MIT — see [LICENSE](LICENSE).
