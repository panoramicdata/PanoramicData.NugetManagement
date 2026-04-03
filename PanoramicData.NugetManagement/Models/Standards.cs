using System.Diagnostics;

namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// Standard file contents and values that all repositories should conform to.
/// These are the opinionated constants maintained in this package.
/// </summary>
public static class Standards
{
	/// <summary>
	/// The fallback SDK version used when detection fails.
	/// </summary>
	private const string _fallbackDotNetSdkVersion = "10.0.201";

	/// <summary>
	/// The latest .NET target framework moniker.
	/// </summary>
	public const string LatestTargetFramework = "net10.0";

	/// <summary>
	/// The latest installed .NET SDK version for global.json, detected via <c>dotnet --list-sdks</c>.
	/// Falls back to <see cref="_fallbackDotNetSdkVersion"/> if detection fails.
	/// </summary>
	public static string LatestDotNetSdkVersion => field ??= DetectLatestSdkVersion();

	/// <summary>
	/// Detects the latest installed .NET SDK version by running <c>dotnet --list-sdks</c>
	/// and selecting the highest version that matches the current major version (10).
	/// </summary>
	private static string DetectLatestSdkVersion()
	{
		try
		{
			using var process = new Process();
			process.StartInfo = new ProcessStartInfo
			{
				FileName = "dotnet",
				Arguments = "--list-sdks",
				RedirectStandardOutput = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			process.Start();
			var output = process.StandardOutput.ReadToEnd();
			process.WaitForExit(5000);

			// Parse lines like "10.0.201 [C:\Program Files\dotnet\sdk]"
			// and pick the highest version matching the current major version prefix.
			var majorPrefix = LatestTargetFramework.Replace("net", "").Split('.')[0] + ".";
			var best = output
				.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(line => line.Split(' ', 2)[0])
				.Where(v => v.StartsWith(majorPrefix, StringComparison.Ordinal))
				.OrderByDescending(v => v, StringComparer.Ordinal)
				.FirstOrDefault();

			return best ?? _fallbackDotNetSdkVersion;
		}
		catch
		{
			return _fallbackDotNetSdkVersion;
		}
	}

	/// <summary>
	/// The latest .NET version specifier for CI workflows.
	/// </summary>
	public const string LatestDotNetVersionSpecifier = "10.0.x";

	/// <summary>
	/// The standard actions/checkout version used in Meraki.Api workflow.
	/// </summary>
	public const string LatestActionsCheckoutVersion = "v4";

	/// <summary>
	/// The standard actions/setup-dotnet version used in Meraki.Api workflow.
	/// </summary>
	public const string LatestActionsSetupDotnetVersion = "v4";

	/// <summary>
	/// The expected copyright holder name.
	/// </summary>
	public const string CopyrightHolder = "Panoramic Data Limited";

	/// <summary>
	/// The expected license type.
	/// </summary>
	public const string LicenseType = "MIT";

	/// <summary>
	/// The expected HTTP client package name.
	/// </summary>
	public const string ExpectedHttpClientPackage = "Refit";

	/// <summary>
	/// The standard SECURITY.md content for all repositories.
	/// </summary>
	public const string SecurityMdContent = """
		# Security Policy

		## Supported Versions

		Only the latest released version is supported with security updates.

		## Reporting a Vulnerability

		If you discover a security vulnerability, please report it responsibly.

		**Do NOT open a public GitHub issue.**

		Instead, please email security@panoramicdata.com with:

		- A description of the vulnerability
		- Steps to reproduce the issue
		- Any relevant logs or screenshots

		We will acknowledge receipt within 48 hours and aim to provide a fix or mitigation within 7 business days.

		## Disclosure Policy

		We follow a coordinated disclosure process. We ask that you:

		1. Allow us reasonable time to investigate and address the issue
		2. Avoid exploiting the vulnerability beyond what is necessary to demonstrate it
		3. Do not disclose the issue publicly until we have released a fix

		Thank you for helping keep our software and users safe.
		""";

	/// <summary>
	/// The standard CONTRIBUTING.md content for all repositories.
	/// </summary>
	public const string ContributingMdContent = """
		# Contributing

		Thank you for your interest in contributing to this project!

		## How to Contribute

		1. **Fork** the repository
		2. **Create a branch** for your feature or fix (`git checkout -b feature/my-feature`)
		3. **Make your changes** following the coding standards below
		4. **Write or update tests** as appropriate
		5. **Ensure the build passes** with zero errors, zero warnings, and zero messages
		6. **Submit a Pull Request** against the `main` branch

		## Coding Standards

		- All public members must have XML documentation comments
		- Use `System.Text.Json` — do not introduce `Newtonsoft.Json`
		- Use Refit for HTTP client interfaces
		- Use file-scoped namespaces
		- Use the `required` keyword for DTO properties where appropriate
		- Ensure `TreatWarningsAsErrors` remains enabled
		- All code must compile with zero diagnostics

		## Testing

		- Use xUnit v3 for all tests
		- Use AwesomeAssertions for fluent assertions
		- Ensure all existing tests pass before submitting a PR

		## License

		By contributing, you agree that your contributions will be licensed under the MIT License.
		""";

	/// <summary>
	/// The standard dependabot.yml content for all repositories.
	/// </summary>
	public const string DependabotYmlContent = """
		version: 2
		updates:
		  - package-ecosystem: "nuget"
		    directory: "/"
		    schedule:
		      interval: "weekly"
		    open-pull-requests-limit: 10
		  - package-ecosystem: "github-actions"
		    directory: "/"
		    schedule:
		      interval: "weekly"
		    open-pull-requests-limit: 5
		""";

	/// <summary>
	/// The standard global.json content pinning the SDK version.
	/// </summary>
	public static string GlobalJsonContent => $$"""
		{
		  "sdk": {
			"version": "{{LatestDotNetSdkVersion}}",
			"rollForward": "latestFeature"
		  }
		}
		""";

	/// <summary>
	/// The standard version.json content for Nerdbank.GitVersioning.
	/// </summary>
	public const string VersionJsonContent = """
		{
		  "$schema": "https://raw.githubusercontent.com/dotnet/Nerdbank.GitVersioning/main/src/NerdBank.GitVersioning/version.schema.json",
		  "version": "1.0",
		  "publicReleaseRefSpec": [
			"^refs/heads/main$"
		  ]
		}
		""";

	/// <summary>
	/// The standard CodeQL workflow content for GitHub Actions.
	/// </summary>
	public const string CodeQlWorkflowContent = """
		name: "CodeQL"

		on:
		  push:
			branches: [ "main" ]
		  pull_request:
			branches: [ "main" ]
		  schedule:
			- cron: '0 6 * * 1'

		jobs:
		  analyze:
			name: Analyze
			runs-on: ubuntu-latest
			permissions:
			  actions: read
			  contents: read
			  security-events: write

			strategy:
			  fail-fast: false
			  matrix:
				language: [ 'csharp' ]

			steps:
			- name: Checkout repository
			  uses: actions/checkout@v4

			- name: Initialize CodeQL
			  uses: github/codeql-action/init@v3
			  with:
				languages: ${{ matrix.language }}

			- name: Autobuild
			  uses: github/codeql-action/autobuild@v3

			- name: Perform CodeQL Analysis
			  uses: github/codeql-action/analyze@v3
			  with:
				category: "/language:${{ matrix.language }}"
		""";

	/// <summary>
	/// The standard .editorconfig content for .NET repositories.
	/// </summary>
	public const string EditorConfigContent = """
		root = true

		[*]
		indent_style = tab
		indent_size = 4
		end_of_line = crlf
		charset = utf-8
		trim_trailing_whitespace = true
		insert_final_newline = true

		[*.cs]
		csharp_style_namespace_declarations = file_scoped:error
		csharp_using_directive_placement = outside_namespace:error

		[*.{xml,csproj,props,targets}]
		indent_style = tab
		""";

	/// <summary>
	/// The standard Publish.ps1 script content for tag-based publishing.
	/// </summary>
	public const string PublishPs1Content = """
		# Ensure we are on the main branch
		$branch = git rev-parse --abbrev-ref HEAD
		if ($branch -ne 'main') {
			Write-Error "Not on main branch. Current branch: $branch"
			exit 1
		}

		# Ensure working tree is clean
		$status = git status --porcelain
		if ($status) {
			Write-Error "Working tree is not clean."
			exit 1
		}

		# Ensure we are up to date with origin
		git fetch origin main --quiet
		$behind = git rev-list --count HEAD..origin/main
		if ($behind -gt 0) {
			Write-Error "Local branch is behind origin/main by $behind commit(s)."
			exit 1
		}

		# Get version from Nerdbank.GitVersioning
		$versionJson = nbgv get-version -f json | ConvertFrom-Json
		$version = $versionJson.NuGetPackageVersion
		Write-Host "Version: $version"

		# Check if tag already exists
		$existingTag = git tag -l $version
		if ($existingTag) {
			Write-Error "Tag $version already exists."
			exit 1
		}

		# Create and push tag
		git tag $version
		git push origin $version
		Write-Host "Tag $version pushed. CI will publish the package."
		""";
}
