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
- **NuGet Hygiene** — snupkg symbols, GeneratePackageOnBuild, PackageReadmeFile
- **Target Framework** — Latest .NET version
- **Build Quality** — TreatWarningsAsErrors, Nullable, ImplicitUsings
- **Code Quality** — .editorconfig, file-scoped namespaces, Codacy, CodeQL
- **Testing** — Test project existence, xUnit v3, coverlet
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
