using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// Standard file contents and values that all repositories should conform to.
/// These are the opinionated constants maintained in this package.
/// </summary>
public static class Standards
{
	/// <summary>
	/// The .NET channel every version constant below is derived from: the newest channel Microsoft
	/// still supports, fetched from the published release index.
	/// </summary>
	private static DotNetChannelStandard Channel => DotNetReleaseCatalog.Default.Current;

	/// <summary>
	/// The latest .NET target framework moniker, for example <c>net10.0</c>.
	/// </summary>
	public static string LatestTargetFramework => Channel.TargetFramework;

	/// <summary>
	/// The SDK version to pin in <c>global.json</c>: the feature-band floor for the supported
	/// channel, for example <c>10.0.100</c>.
	/// </summary>
	/// <remarks>
	/// Deliberately the floor rather than the channel's newest SDK. <c>rollForward</c> never rolls
	/// down, so pinning <c>10.0.400</c> makes a 4xx install a build requirement for everyone: a
	/// machine whose newest band is 3xx cannot run any dotnet command. Nor is security a reason to
	/// pin higher — Microsoft services every live feature band in the same release, so <c>10.0.111</c>
	/// carries the same fixes as <c>10.0.400</c>. What the pin needs alongside it is a band-crossing
	/// <c>rollForward</c>, which is what VER-03 checks.
	/// </remarks>
	public static string DotNetSdkPinVersion => Channel.SdkPinVersion;

	/// <summary>
	/// The latest .NET version specifier for CI workflows, for example <c>10.0.x</c>.
	/// </summary>
	public static string LatestDotNetVersionSpecifier => Channel.VersionSpecifier;

	/// <summary>
	/// The <c>rollForward</c> value the standard global.json uses. Band-crossing, so the pinned
	/// version acts as a floor meaning "a .NET N SDK" rather than tying the build to one feature band.
	/// </summary>
	public const string SdkRollForward = "latestMinor";

	/// <summary>
	/// The <c>test.runner</c> value that opts <c>dotnet test</c> into the Microsoft.Testing.Platform
	/// experience. Must be spelled exactly like this: the internal identifier
	/// <c>MicrosoftTestingPlatform</c> parses but is rejected at runtime.
	/// </summary>
	public const string MtpTestRunnerName = "Microsoft.Testing.Platform";

	/// <summary>
	/// The expected xunit.v3 package version.
	/// </summary>
	public const string XunitV3Version = "4.0.0";

	/// <summary>
	/// The expected Microsoft.NET.Test.Sdk package version.
	/// </summary>
	public const string MicrosoftNetTestSdkVersion = "18.9.0";

	/// <summary>
	/// The expected code coverage collector package. The Microsoft.Testing.Platform replacement for
	/// coverlet, which only functions as a VSTest data collector.
	/// </summary>
	public const string CodeCoveragePackage = "Microsoft.Testing.Extensions.CodeCoverage";

	/// <summary>
	/// The expected <see cref="CodeCoveragePackage"/> version. Shares a version line with
	/// <see cref="MicrosoftNetTestSdkVersion"/>, though the two can be a release apart.
	/// </summary>
	public const string CodeCoverageVersion = "18.10.0";

	/// <summary>
	/// Coverlet packages that are inert under Microsoft.Testing.Platform: both hook the VSTest
	/// target that no longer runs on the .NET 10 SDK, so their presence signals coverage config
	/// that looks alive but collects nothing.
	/// </summary>
	public static readonly string[] DeadCoverletPackages = ["coverlet.collector", "coverlet.msbuild"];

	/// <summary>
	/// The VSTest adapter for xUnit, unnecessary under Microsoft.Testing.Platform.
	/// </summary>
	public const string VsTestAdapterPackage = "xunit.runner.visualstudio";

	/// <summary>
	/// The minimum acceptable actions/checkout major version. Repositories at or above this pass;
	/// the value is derived from the highest version in use across the organization's repositories.
	/// </summary>
	public const string LatestActionsCheckoutVersion = "v7";

	/// <summary>
	/// The minimum acceptable actions/setup-dotnet major version (repositories at or above this pass).
	/// </summary>
	public const string LatestActionsSetupDotnetVersion = "v6";

	/// <summary>
	/// The minimum acceptable actions/upload-artifact major version.
	/// </summary>
	public const string LatestActionsUploadArtifactVersion = "v7";

	/// <summary>
	/// The minimum acceptable actions/download-artifact major version.
	/// </summary>
	public const string LatestActionsDownloadArtifactVersion = "v8";

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
	/// The standard NuGet user for Trusted Publishing login.
	/// </summary>
	public const string NuGetUser = "david_n_m_bond";

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
	/// The standard global.json content pinning the SDK version, and opting <c>dotnet test</c> into
	/// the Microsoft.Testing.Platform runner when — and only when — the repository can run on it.
	/// </summary>
	/// <param name="includeTestRunner">
	/// True for a repository on xunit.v3, which needs the opt-in to run its tests at all on the .NET
	/// 10 SDK. False otherwise: a repository still on xunit v2 and VSTest cannot satisfy the opt-in,
	/// and handing it one leaves `dotnet test` unable to run anything.
	/// </param>
	public static string GetGlobalJsonContent(bool includeTestRunner)
		=> includeTestRunner
			? $$"""
				{
				  "sdk": {
					"version": "{{DotNetSdkPinVersion}}",
					"rollForward": "{{SdkRollForward}}"
				  },
				  "test": {
					"runner": "{{MtpTestRunnerName}}"
				  }
				}
				"""
			: $$"""
				{
				  "sdk": {
					"version": "{{DotNetSdkPinVersion}}",
					"rollForward": "{{SdkRollForward}}"
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
			  uses: actions/checkout@v7

			- name: Initialize CodeQL
			  uses: github/codeql-action/init@v4
			  with:
				languages: ${{ matrix.language }}

			- name: Autobuild
			  uses: github/codeql-action/autobuild@v4

			- name: Perform CodeQL Analysis
			  uses: github/codeql-action/analyze@v4
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
	/// The standard ci.yml workflow content for GitHub Actions.
	/// </summary>
	public const string CiWorkflowContent = """
		name: CI

		on:
		  push:
			branches: [main]
		  pull_request:
			branches: [main]
		  release:
			types: [published]

		jobs:
		  build:
			runs-on: ubuntu-latest
			steps:
			- name: Checkout
			  uses: actions/checkout@v7
			  with:
				fetch-depth: 0

			- name: Setup .NET
			  uses: actions/setup-dotnet@v6
			  with:
				dotnet-version: 10.0.x

			- name: Restore
			  run: dotnet restore

			- name: Build
			  run: dotnet build --configuration Release --no-restore

			- name: Pack
			  run: dotnet pack --configuration Release --no-build --output ./artifacts

			- name: Upload artifacts
			  uses: actions/upload-artifact@v7
			  with:
				name: packages
				path: ./artifacts/*.nupkg
		""";

	/// <summary>
	/// The standard CI workflow content for GitHub Actions with Trusted Publishing.
	/// Used by CI-08 when the existing workflow is missing required snippets.
	/// </summary>
	public static string GetTrustedPublishingCiWorkflowContent(string nuGetUser)
		=> string.Join("\n",
		[
				"name: CI",
				string.Empty,
				"on:",
				"  push:",
				"    branches: [main]",
				"    tags: ['[0-9]*.[0-9]*.[0-9]*']",
				"  pull_request:",
				"    branches: [main]",
				string.Empty,
				"jobs:",
				"  build:",
				"    runs-on: ubuntu-latest",
				"    steps:",
				"    - name: Checkout",
				"      uses: actions/checkout@v7",
				"      with:",
				"        fetch-depth: 0",
				string.Empty,
				"    - name: Setup .NET",
				"      uses: actions/setup-dotnet@v6",
				"      with:",
				"        dotnet-version: 10.0.x",
				string.Empty,
				"    - name: Restore",
				"      run: dotnet restore",
				string.Empty,
				"    - name: Build",
				"      run: dotnet build --configuration Release --no-restore",
				string.Empty,
				"    - name: Pack",
				"      run: dotnet pack --configuration Release --no-build --output ./artifacts",
				string.Empty,
				"    - name: Upload artifacts",
				"      uses: actions/upload-artifact@v7",
				"      with:",
				"        name: packages",
				"        path: ./artifacts/*.nupkg",
				string.Empty,
				"  publish:",
				"    needs: build",
				"    runs-on: ubuntu-latest",
				"    if: startsWith(github.ref, 'refs/tags/')",
				"    permissions:",
				"      id-token: write",
				"    steps:",
				"    - name: Download artifacts",
				"      uses: actions/download-artifact@v8",
				"      with:",
				"        name: packages",
				"        path: ./artifacts",
				string.Empty,
				"    - name: Setup .NET",
				"      uses: actions/setup-dotnet@v6",
				"      with:",
				"        dotnet-version: 10.0.x",
				string.Empty,
				"    - name: Login to NuGet",
				"      id: login",
				"      uses: NuGet/login@v1",
				"      with:",
				$"        user: {nuGetUser}",
				string.Empty,
				"    - name: Push to NuGet",
			 "      run: dotnet nuget push ./artifacts/*.nupkg --api-key ${{ steps.login.outputs.NUGET_API_KEY }} --source https://api.nuget.org/v3/index.json --skip-duplicate"
		]);

	/// <summary>
	/// The standard .gitignore content for .NET repositories.
	/// </summary>
	public const string GitignoreContent = """
		## Ignore Visual Studio temporary files, build results, and
		## files generated by popular Visual Studio add-ons.

		# User-specific files
		*.rsuser
		*.suo
		*.user
		*.userosscache
		*.sln.docstates

		# Build results
		[Dd]ebug/
		[Dd]ebugPublic/
		[Rr]elease/
		[Rr]eleases/
		x64/
		x86/
		[Ww][Ii][Nn]32/
		[Aa][Rr][Mm]/
		[Aa][Rr][Mm]64/
		bld/
		[Bb]in/
		[Oo]bj/
		[Ll]og/
		[Ll]ogs/

		# Visual Studio cache/options directory
		.vs/

		# NuGet Packages
		*.nupkg
		*.snupkg
		**/[Pp]ackages/*
		!**/[Pp]ackages/build/

		# Test Results
		[Tt]est[Rr]esult*/
		[Bb]uild[Ll]og.*
		TestResult.xml

		# dotnet tool
		.config/dotnet-tools.json

		# JetBrains Rider
		.idea/
		*.sln.iml
		""";

	/// <summary>
	/// The standard MIT LICENSE text.
	/// </summary>
	public const string MitLicenseContent = """
		MIT License

		Copyright (c) Panoramic Data Limited

		Permission is hereby granted, free of charge, to any person obtaining a copy
		of this software and associated documentation files (the "Software"), to deal
		in the Software without restriction, including without limitation the rights
		to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
		copies of the Software, and to permit persons to whom the Software is
		furnished to do so, subject to the following conditions:

		The above copyright notice and this permission notice shall be included in all
		copies or substantial portions of the Software.

		THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
		IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
		FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
		AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
		LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
		OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
		SOFTWARE.
		""";

	/// <summary>
	/// The standard Publish.ps1 script content for tag-based publishing.
	/// </summary>
	public const string PublishPs1Content = """
		param(
			# Skips waiting for the release run. The tag is still pushed, but nothing confirms a package
			# reached nuget.org — use it only if you are checking the run yourself.
			[switch]$SkipPublishVerification
		)

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

		# Checked before anything is pushed, because pushing the tag is the step that cannot be taken back.
		# Without the GitHub CLI there is no way to confirm the release run succeeded, and an unverified
		# publish is how repositories end up months behind their newest tag with nobody noticing.
		if (-not $SkipPublishVerification) {
			$gh = Get-Command gh -ErrorAction SilentlyContinue
			if (-not $gh) {
				Write-Error "The GitHub CLI (gh) is required to verify that the package publishes. Install it from https://cli.github.com, or re-run with -SkipPublishVerification to publish without verification."
				exit 1
			}

			gh auth status 2>&1 | Out-Null
			if ($LASTEXITCODE -ne 0) {
				Write-Error "The GitHub CLI is not authenticated. Run 'gh auth login', or re-run with -SkipPublishVerification to publish without verification."
				exit 1
			}
		}

		# Get version from Nerdbank.GitVersioning via the project's MSBuild targets (the
		# referenced NuGet package), so this does not depend on the global 'nbgv' CLI tool
		# being installed or on PATH.
		$packableProject = Get-ChildItem -Recurse -Filter *.csproj |
			Where-Object { $_.FullName -notmatch '[\\/]obj[\\/]' -and (Get-Content $_.FullName -Raw) -match 'Nerdbank\.GitVersioning' } |
			Select-Object -First 1
		if (-not $packableProject) {
			Write-Error "Could not find a packable project referencing Nerdbank.GitVersioning."
			exit 1
		}
		$buildOutput = dotnet build $packableProject.FullName -t:GetBuildVersion --getProperty:NuGetPackageVersion -nologo -v:quiet -p:TreatWarningsAsErrors=false
		if ($LASTEXITCODE -ne 0) {
			Write-Error "Failed to determine version from Nerdbank.GitVersioning.`n$buildOutput"
			exit 1
		}
		$version = ($buildOutput | Select-Object -Last 1).ToString().Trim()
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
		Write-Host "Tag $version pushed."

		if ($SkipPublishVerification) {
			Write-Warning "Not waiting for the release run (-SkipPublishVerification). Nothing has confirmed that a package reached nuget.org."
			exit 0
		}

		# The repository the run belongs to, read from the remote rather than assumed.
		$originUrl = git remote get-url origin
		$repoFullName = ($originUrl -replace '^.*github\.com[:/]', '') -replace '\.git$', ''

		Write-Host "Waiting for the release run for $version..."

		# The run takes a few seconds to appear after the tag push.
		$runId = $null
		for ($attempt = 1; $attempt -le 12 -and -not $runId; $attempt++) {
			Start-Sleep -Seconds 5
			$runListJson = gh run list --repo $repoFullName --branch $version --limit 1 --json databaseId 2>$null
			if ($LASTEXITCODE -eq 0 -and $runListJson) {
				$runList = $runListJson | ConvertFrom-Json
				if ($runList.Count -gt 0) { $runId = $runList[0].databaseId }
			}
		}

		if (-not $runId) {
			Write-Error "Tag $version was pushed but no run appeared for it. Check https://github.com/$repoFullName/actions — the workflow may not trigger on tags."
			exit 1
		}

		Write-Host "Run: https://github.com/$repoFullName/actions/runs/$runId"
		gh run watch $runId --repo $repoFullName --exit-status --interval 20
		$runExitCode = $LASTEXITCODE

		if ($runExitCode -ne 0) {
			Write-Host ""
			Write-Host "The release run did not succeed: https://github.com/$repoFullName/actions/runs/$runId" -ForegroundColor Red

			# A refused job — an exhausted Actions budget, for instance — fails before any step runs, so it
			# has no failed step to report. The check-run annotation is the only place the reason appears.
			$jobId = gh api "repos/$repoFullName/actions/runs/$runId/jobs" --jq '.jobs[0].id' 2>$null
			if ($LASTEXITCODE -eq 0 -and $jobId) {
				$annotation = gh api "repos/$repoFullName/check-runs/$jobId/annotations" --jq '.[0].message' 2>$null
				if ($LASTEXITCODE -eq 0 -and $annotation) {
					Write-Host "Reason: $annotation" -ForegroundColor Red
				}
			}

			Write-Host ""
			Write-Host "Tag $version is pushed but no package was published. Once the cause is fixed:" -ForegroundColor Yellow
			Write-Host "  gh run rerun $runId --repo $repoFullName --failed" -ForegroundColor Cyan
			exit 1
		}

		Write-Host "Package $version published." -ForegroundColor Green
		""";
}
