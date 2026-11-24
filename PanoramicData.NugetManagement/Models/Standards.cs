using System.Text.RegularExpressions;
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
	/// pin higher: Microsoft services every live feature band in the same release, so <c>10.0.111</c>
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
	public static string SdkRollForward { get; } = "latestMinor";

	/// <summary>
	/// The <c>test.runner</c> value that opts <c>dotnet test</c> into the Microsoft.Testing.Platform
	/// experience. Must be spelled exactly like this: the internal identifier
	/// <c>MicrosoftTestingPlatform</c> parses but is rejected at runtime.
	/// </summary>
	public static string MtpTestRunnerName { get; } = "Microsoft.Testing.Platform";

	/// <summary>
	/// The expected xunit.v3 package version.
	/// </summary>
	public static string XunitV3Version { get; } = "4.0.0";

	/// <summary>
	/// The expected Microsoft.NET.Test.Sdk package version.
	/// </summary>
	public static string MicrosoftNetTestSdkVersion { get; } = "18.9.0";

	/// <summary>
	/// The expected code coverage collector package. The Microsoft.Testing.Platform replacement for
	/// coverlet, which only functions as a VSTest data collector.
	/// </summary>
	public static string CodeCoveragePackage { get; } = "Microsoft.Testing.Extensions.CodeCoverage";

	/// <summary>
	/// The expected <see cref="CodeCoveragePackage"/> version. Shares a version line with
	/// <see cref="MicrosoftNetTestSdkVersion"/>, though the two can be a release apart.
	/// </summary>
	public static string CodeCoverageVersion { get; } = "18.10.0";

	/// <summary>
	/// The code coverage collector for a repository still on VSTest, where
	/// <see cref="CodeCoveragePackage"/> cannot be used at all: it depends on
	/// Microsoft.Testing.Platform, so referencing it selects a platform xunit v2 cannot serve.
	/// </summary>
	public static string VsTestCodeCoveragePackage { get; } = "coverlet.collector";

	/// <summary>
	/// The expected <see cref="VsTestCodeCoveragePackage"/> version, matching its nuget-floors.json
	/// floor.
	/// </summary>
	public static string VsTestCodeCoverageVersion { get; } = "10.0.1";

	/// <summary>
	/// Coverlet packages that are inert under Microsoft.Testing.Platform: both hook the VSTest
	/// target that no longer runs on the .NET 10 SDK, so their presence signals coverage config
	/// that looks alive but collects nothing. Dead only there: on a repository still on VSTest,
	/// coverlet is the collector that works, which is why TST-04 reports these only once xunit.v3
	/// is in use.
	/// </summary>
	public static readonly string[] DeadCoverletPackages = [VsTestCodeCoveragePackage, "coverlet.msbuild"];

	/// <summary>
	/// The VSTest adapter for xUnit, unnecessary under Microsoft.Testing.Platform.
	/// </summary>
	public static string VsTestAdapterPackage { get; } = "xunit.runner.visualstudio";

	/// <summary>
	/// The expected AwesomeAssertions version. AwesomeAssertions is the API-compatible fork of
	/// FluentAssertions, which from version 8 requires a paid Xceed licence for commercial use.
	/// </summary>
	public static string AwesomeAssertionsVersion { get; } = "9.6.0";

	/// <summary>
	/// The expected AwesomeAssertions.Analyzers version.
	/// </summary>
	public static string AwesomeAssertionsAnalyzersVersion { get; } = "9.0.8";

	/// <summary>
	/// The minimum acceptable actions/checkout major version. Repositories at or above this pass;
	/// the value is derived from the highest version in use across the organization's repositories.
	/// </summary>
	public static string LatestActionsCheckoutVersion { get; } = "v7";

	/// <summary>
	/// The minimum acceptable actions/setup-dotnet major version (repositories at or above this pass).
	/// </summary>
	public static string LatestActionsSetupDotnetVersion { get; } = "v6";

	/// <summary>
	/// The minimum acceptable actions/upload-artifact major version.
	/// </summary>
	public static string LatestActionsUploadArtifactVersion { get; } = "v7";

	/// <summary>
	/// The minimum acceptable actions/download-artifact major version.
	/// </summary>
	public static string LatestActionsDownloadArtifactVersion { get; } = "v8";

	/// <summary>
	/// The expected copyright holder name.
	/// </summary>
	public static string CopyrightHolder { get; } = "Panoramic Data Limited";

	/// <summary>
	/// The default expertise statement README-05 puts at the top of every package README, as one
	/// Markdown line. <c>{PackageName}</c> is replaced with the repository name.
	/// </summary>
	public static string ExpertiseBannerTemplate { get; } =
		"> **Need help with APIs or .NET?** Panoramic Data are experts in APIs and .NET, and we can help with your development. "
		+ "[Contact us](https://www.panoramicdata.com/contact-us) or see our [software services](https://www.panoramicdata.com/services/software).";

	/// <summary>
	/// The expected license type.
	/// </summary>
	public static string LicenseType { get; } = "MIT";

	/// <summary>
	/// The expected HTTP client package name.
	/// </summary>
	public static string ExpectedHttpClientPackage { get; } = "Refit";

	/// <summary>
	/// The standard NuGet user for Trusted Publishing login.
	/// </summary>
	public static string NuGetUser { get; } = "david_n_m_bond";

	/// <summary>
	/// The SECURITY.md this tool wrote while it still published a mailbox address. Replaceable when a
	/// file holds exactly this, because it was generated; Codacy flags the address as PII.
	/// </summary>
	public static string LegacySecurityMdContent { get; } = """
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
	/// The standard SECURITY.md content for a repository.
	/// </summary>
	/// <remarks>
	/// Points at GitHub's private vulnerability reporting rather than a mailbox: reports stay private
	/// and tracked per repository, there is no inbox to watch, and no address sits in the file for
	/// Codacy to flag as PII. COM-05 checks the setting is actually switched on.
	/// </remarks>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	public static string GetSecurityMdContent(string repositoryFullName) => $"""
		# Security Policy

		## Supported Versions

		Only the latest released version is supported with security updates.

		## Reporting a Vulnerability

		If you discover a security vulnerability, please report it responsibly.

		**Do not open a public GitHub issue.**

		Instead, use GitHub's private vulnerability reporting: open this repository's **Security** tab
		and choose **Report a vulnerability**, or go straight to
		https://github.com/{repositoryFullName}/security/advisories/new

		Please include:

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
	public static string ContributingMdContent { get; } = """
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
		- Use `System.Text.Json`; do not introduce `Newtonsoft.Json`
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
	/// The heading of the last section of <see cref="ContributingMdContent"/>, the only one a repository may extend.
	/// </summary>
	public static string ContributingLicenseHeading { get; } = "## License";

	/// <summary>
	/// The standard CONTRIBUTING.md for a repository under <paramref name="license"/>: the standard text
	/// for the default licence, otherwise the same guide with a licence sentence that names no licence it is not.
	/// </summary>
	/// <param name="license">The repository's SPDX licence expression.</param>
	public static string ContributingMdContentFor(string license)
		=> LicenseExpressions.IsDefault(license)
			? ContributingMdContent
			: WithContributingLineEndings($"{ContributingMdPreamble()}{ContributingLicenseHeading}\n\nBy contributing, you agree that your contributions will be licensed under the terms in [LICENSE](LICENSE) (SPDX: `{license}`).");

	/// <summary>
	/// Gives \n-separated text the line endings <see cref="ContributingMdContent"/> has, so a generated variant writes like the standard.
	/// </summary>
	/// <param name="text">Text with \n line endings.</param>
	public static string WithContributingLineEndings(string text)
		=> ContributingMdContent.Contains("\r\n", StringComparison.Ordinal)
			? text.Replace("\n", "\r\n", StringComparison.Ordinal)
			: text;

	/// <summary>
	/// Everything in <see cref="ContributingMdContent"/> before its licence section, with \n line endings.
	/// </summary>
	public static string ContributingMdPreamble()
	{
		var standard = ContributingMdContent.Replace("\r\n", "\n", StringComparison.Ordinal);
		return standard[..standard.IndexOf(ContributingLicenseHeading, StringComparison.Ordinal)];
	}

	/// <summary>
	/// The standard dependabot.yml content for all repositories.
	/// </summary>
	public static string DependabotYmlContent { get; } = """
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
	/// the Microsoft.Testing.Platform runner when, and only when, the repository can run on it.
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
	public static string VersionJsonContent { get; } = """
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
	public static string CodeQlWorkflowContent { get; } = """
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
		      with:
		        # nbgv derives the version from commit height and cannot on a shallow clone.
		        fetch-depth: 0

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
	public static string EditorConfigContent { get; } = """
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
	public static string CiWorkflowContent => CiWorkflowTemplate.Replace(DotNetVersionToken, LatestDotNetVersionSpecifier, StringComparison.Ordinal);

	private const string DotNetVersionToken = "{{DOTNET_VERSION}}";

	private const string CiWorkflowTemplate = """
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
		        dotnet-version: {{DOTNET_VERSION}}

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
		new[]
		{
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
				$"        dotnet-version: {LatestDotNetVersionSpecifier}",
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
				$"        dotnet-version: {LatestDotNetVersionSpecifier}",
				string.Empty,
				"    - name: Login to NuGet",
				"      id: login",
				"      uses: NuGet/login@v1",
				"      with:",
				$"        user: {nuGetUser}",
				string.Empty,
				"    - name: Push to NuGet",
			 "      run: dotnet nuget push ./artifacts/*.nupkg --api-key ${{ steps.login.outputs.NUGET_API_KEY }} --source https://api.nuget.org/v3/index.json"
		});

	/// <summary>
	/// The standard .gitignore content for .NET repositories.
	/// </summary>
	public static string GitignoreContent { get; } = """
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
	public static string MitLicenseContent { get; } = """
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
	/// The relative path, from a repository root, to the shared Panoramic Data skills instructions
	/// maintained in the private <c>PanoramicData.Skills</c> sibling repository.
	/// </summary>
	public static string SharedSkillsCopilotInstructionsPath { get; } = "../PanoramicData.Skills/.github/skills/copilot-instructions.md";

	/// <summary>The About section the v1.3 templates carried. It exists only to recognise those files.</summary>
	private static string AboutSectionContent { get; } = """
		## About Panoramic Data

		Panoramic Data Limited is a software company. This repository is a NuGet package that it
		publishes. Its build, CI, versioning, licensing and community files are governed by the open
		source PanoramicData.NugetManagement tool (https://github.com/panoramicdata/PanoramicData.NugetManagement),
		which assesses repositories against a shared set of rules and can apply fixes automatically.
		Files such as CLAUDE.md, AGENTS.md and SECURITY.md may be created or updated
		by that tool.
		""";

	/// <summary>
	/// The instruction AI-06 requires in AGENTS.md. Agents add a co-author trailer to every commit
	/// unless told not to, and Panoramic Data does not want them in its history.
	/// </summary>
	public static string NoAiAttributionInstruction { get; } = "Do not add `Co-Authored-By` trailers or other AI attribution to commit messages or pull requests.";

	/// <summary>The heading of the one section a public AGENTS.md may use to mention Panoramic Data.</summary>
	public static string StaffSectionHeading { get; } = "## PanoramicData staff";

	/// <summary>
	/// The staff section of the public AGENTS.md template in PanoramicData.Skills (#2256), copied byte for
	/// byte: this package is public and cannot read that private checkout. It is deliberately generic.
	/// </summary>
	public static string StaffSectionContent { get; } = """
		## PanoramicData staff

		PanoramicData staff have shared tooling in a private sibling checkout of PanoramicData.Skills. Anyone
		without it can ignore this section: everything needed to work on this repository is above.
		""";

	/// <summary>
	/// The standard AGENTS.md for a public repository: the one instructions file that Claude Code, Codex and
	/// Copilot all read. It is standalone and mentions Panoramic Data only in the staff section.
	/// </summary>
	/// <remarks>
	/// Structured with headings, an identity, boundaries, and tool notes because Codacy's Agentlinter
	/// grades instruction files on exactly those things. It names no other file, because Agentlinter
	/// reports a file name it cannot find in its workspace.
	/// </remarks>
	public static string AgentsMdContent => $"""
		# Agent instructions

		## Identity

		You are a careful, security-conscious contributor to this repository.

		## Scope and boundaries

		- Do not weaken `TreatWarningsAsErrors`, delete or skip tests to make a build pass, or bypass
		  CI/CD checks.
		- Do not commit secrets, credentials, or API tokens.
		- Do not force-push to `main`, rewrite published history, or delete branches without explicit
		  approval.
		- {NoAiAttributionInstruction}

		## Tools

		- Build and test with `dotnet build` / `dotnet test`.
		- Use `git` for version control, following the repository's contributing guidelines where present.

		{StaffSectionContent}

		""";

	/// <summary>Whether <paramref name="content"/> carries the managed block that PanoramicData.Skills writes into its own repositories' AGENTS.md.</summary>
	public static bool HasManagedBlock(string content)
		=> content.Contains("<!-- BEGIN PanoramicData.Skills managed block", StringComparison.Ordinal);

	/// <summary>
	/// What a public AGENTS.md must not contain, once the generic staff section is set aside: a path into
	/// the private checkout, an <c>@import</c> line, or a reference to its skills.
	/// </summary>
	public static IReadOnlyList<string> FindPublicAgentsMdLeaks(string content)
	{
		var text = Normalise(content).Replace(Normalise(StaffSectionContent), string.Empty, StringComparison.Ordinal);
		var leaks = new List<string>();
		foreach (var (pattern, description) in _publicLeakPatterns)
		{
			if (Regex.IsMatch(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(2)))
			{
				leaks.Add(description);
			}
		}

		return leaks;
	}

	private static readonly (string Pattern, string Description)[] _publicLeakPatterns =
	[
		(@"PanoramicData\.Skills", "names the private PanoramicData.Skills repository"),
		(@"\.\./PanoramicData", "uses a path into a sibling PanoramicData checkout"),
		(@"(?m)^\s*@\S", "has an @import line"),
		(@"\.github/(skills|copilot-instructions)", "refers to shared skills or a copilot instructions file")
	];

	/// <summary>
	/// Whether every line of <paramref name="content"/> is a line this tool wrote in a template it has
	/// shipped, so that nothing a person wrote is lost when the file is replaced or deleted.
	/// </summary>
	/// <param name="content">The file's content.</param>
	/// <param name="templates">The templates this tool has generated for that file.</param>
	public static bool IsGeneratedByThisTool(string content, params string[] templates)
	{
		var allowed = new HashSet<string>(StringComparer.Ordinal);
		foreach (var template in templates)
		{
			AddLines(allowed, template);
			foreach (var (current, earlier) in _earlierPhrasings)
			{
				AddLines(allowed, template.Replace(current, earlier, StringComparison.Ordinal));
			}
		}

		return Normalise(content)
			.Split('\n')
			.Select(line => line.Trim())
			.Where(line => line.Length > 0)
			.All(line => allowed.Contains(line) || Regex.IsMatch(line, @"^Version: \d+\.\d+$", RegexOptions.None, TimeSpan.FromSeconds(2)));

		static void AddLines(HashSet<string> set, string text)
		{
			foreach (var line in Normalise(text).Split('\n'))
			{
				set.Add(line.Trim());
			}
		}
	}

	/// <summary>
	/// Wordings the v1.2 templates used, each paired with the v1.3 text that replaced it. The em dash is
	/// written as an escape so this file holds none.
	/// </summary>
	private static readonly (string Current, string Earlier)[] _earlierPhrasings =
	[
		("AGENTS.md and SECURITY.md", "AGENTS.md, SECURITY.md and CONTRIBUTING.md"),
		("following the repository's contributing guidelines where present", "following `CONTRIBUTING.md` where present"),
		("# Agent instructions", "# Agent Instructions"),
		("starting work, as it documents", "starting work — it documents")
	];

	/// <summary>
	/// The CLAUDE.md this tool wrote as template v1.3. It is no longer generated: while a CLAUDE.md exists,
	/// Claude Code ignores AGENTS.md. Kept so that file can be recognised and deleted.
	/// </summary>
	public static string PreviousClaudeMdContent => $"""
		# CLAUDE.md

		Version: 1.3

		## Identity

		You are Claude Code, acting as a careful, security-conscious contributor to this repository,
		following Panoramic Data's engineering conventions.

		{AboutSectionContent}

		## Scope and boundaries

		- Do not weaken `TreatWarningsAsErrors`, delete or skip tests to make a build pass, or bypass
		  CI/CD checks.
		- Do not commit secrets, credentials, or API tokens.
		- Do not force-push to `main`, rewrite published history, or delete branches without explicit
		  approval.
		- {NoAiAttributionInstruction}

		## Tools

		- Build and test with `dotnet build` / `dotnet test`.
		- Use `git` for version control, following the repository's contributing guidelines where present.

		## Shared instructions

		@.github/copilot-instructions.md
		@{SharedSkillsCopilotInstructionsPath}

		The second import above is optional: if the private `PanoramicData.Skills` sibling repository
		is not checked out next to this one, Claude Code silently skips it.
		""";

	/// <summary>
	/// The AGENTS.md this tool wrote as template v1.3, which told agents to read the private Skills
	/// checkout. It is no longer generated, and is kept so that file can be recognised and replaced.
	/// </summary>
	public static string PreviousAgentsMdContent => $"""
		# Agent instructions

		Version: 1.3

		Read `.github/copilot-instructions.md` for repository-specific conventions.

		{AboutSectionContent}

		## Shared Panoramic Data conventions (optional)

		Panoramic Data maintains shared skills in a private sibling repository, reached from this
		repository's root at:

		    {SharedSkillsCopilotInstructionsPath}

		If that path exists, read it before starting work, as it documents organization-wide
		conventions and skills.

		**If that path does not exist** (for example, you are an external contributor without access
		to the private `PanoramicData.Skills` repository), ignore this section and continue. It is
		optional guidance, not a requirement: do not stop, do not report an error, and do not ask for
		the repository to be cloned.

		## Commits

		{NoAiAttributionInstruction}
		""";

	/// <summary>
	/// The CLAUDE.md this tool wrote before the template gained headings, an identity, boundaries and
	/// tool notes. Files holding exactly this were generated, never hand-written, so they are safe to
	/// replace; anything else is someone's own and is left alone.
	/// </summary>
	public static string LegacyClaudeMdContent => $"""
		@.github/copilot-instructions.md
		@{SharedSkillsCopilotInstructionsPath}
		""";

	/// <summary>
	/// The AGENTS.md this tool wrote before the template carried a version marker. Replaceable for the
	/// same reason as <see cref="LegacyClaudeMdContent"/>.
	/// </summary>
	public static string LegacyAgentsMdContent => $"""
		# Agent Instructions

		Read `.github/copilot-instructions.md` for repository-specific conventions.

		## Shared Panoramic Data conventions (optional)

		Panoramic Data maintains shared skills in a private sibling repository, reached from this
		repository's root at:

		    {SharedSkillsCopilotInstructionsPath}

		If that path exists, read it before starting work, as it documents organization-wide
		conventions and skills.

		**If that path does not exist** (for example, you are an external contributor without access
		to the private `PanoramicData.Skills` repository), ignore this section and continue. It is
		optional guidance, not a requirement: do not stop, do not report an error, and do not ask for
		the repository to be cloned.
		""";

	/// <summary>
	/// Whether <paramref name="content"/> is, line endings and surrounding whitespace aside, exactly
	/// <paramref name="generated"/>.
	/// </summary>
	public static bool IsGeneratedContent(string content, string generated)
		=> Normalise(content) == Normalise(generated);

	private static string Normalise(string text) => text.Replace("\r\n", "\n").Trim();

	/// <summary>
	/// The standard Publish.ps1 script content for tag-based publishing.
	/// </summary>
	public static string PublishPs1Content { get; } = """
		param(
			# Skips waiting for the release run. The tag is still pushed, but nothing confirms a package
			# reached nuget.org; use it only if you are checking the run yourself.
			[switch]$SkipPublishVerification
		)

		$InformationPreference = 'Continue'

		# Act on this script's own repository, whatever directory it was started from.
		Set-Location -LiteralPath $PSScriptRoot

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

			# This only reads the stored login. The "status" subcommand makes an API call instead, so a GitHub
			# rate limit makes it report "The token in keyring is invalid" for a perfectly good login.
			gh auth token 2>&1 | Out-Null
			if ($LASTEXITCODE -ne 0) {
				Write-Error "The GitHub CLI has no stored login. Run 'gh auth login', or re-run with -SkipPublishVerification to publish without verification."
				exit 1
			}
		}

		# Get version from Nerdbank.GitVersioning via the project's MSBuild targets (the
		# referenced NuGet package), so this does not depend on the global 'nbgv' CLI tool
		# being installed or on PATH.
		#
		# Only projects git tracks in this checkout are considered. A recursive file search also walks
		# git-ignored folders such as .claude/worktrees/, where each agent worktree holds a full checkout
		# on its own branch: versioning one of those yields a version that is not a public release.
		$projects = git ls-files '*.csproj' | Where-Object { Test-Path $_ }
		$packableProject = $projects |
			Where-Object { (Get-Content $_ -Raw) -match 'Nerdbank\.GitVersioning' } |
			Select-Object -First 1
		if (-not $packableProject) {
			# Nerdbank may be referenced once, from a Directory.Build.props; any project it covers has the same
			# version, but a packable one is preferred.
			$prefixes = @(git ls-files '*Directory.Build.props' |
				Where-Object { (Split-Path $_ -Leaf) -eq 'Directory.Build.props' -and (Test-Path $_) -and (Get-Content $_ -Raw) -match 'Nerdbank\.GitVersioning' } |
				ForEach-Object { $_.Substring(0, $_.Length - 'Directory.Build.props'.Length) })
			$covered = @($projects | Where-Object { $project = $_; @($prefixes | Where-Object { $project.StartsWith($_, [StringComparison]::Ordinal) }).Count -gt 0 })
			$packableProject = @($covered | Where-Object { (Get-Content $_ -Raw) -notmatch '<IsPackable>\s*false\s*</IsPackable>' }) + $covered |
				Select-Object -First 1
		}
		if (-not $packableProject) {
			Write-Error "Could not find a tracked project referencing Nerdbank.GitVersioning."
			exit 1
		}
		$buildOutput = dotnet build $packableProject -t:GetBuildVersion --getProperty:NuGetPackageVersion -nologo -v:quiet -p:TreatWarningsAsErrors=false
		if ($LASTEXITCODE -ne 0) {
			Write-Error "Failed to determine version from Nerdbank.GitVersioning.`n$buildOutput"
			exit 1
		}
		$version = ($buildOutput | Select-Object -Last 1).ToString().Trim()
		Write-Information "Version: $version"

		# On main the version must be a public release (1.2.3). Anything else, such as 1.2.3-gabcdef,
		# would be tagged with a name that misses publicReleaseRefSpec and publish a prerelease.
		if ($version -notmatch '^\d+\.\d+\.\d+$') {
			Write-Error "Version '$version' is not a public-release version (expected major.minor.patch). Not tagging."
			exit 1
		}

		# Check if tag already exists
		$existingTag = git tag -l $version
		if ($existingTag) {
			Write-Error "Tag $version already exists."
			exit 1
		}

		# Create and push tag. A push can fail transiently (a dropped connection, a DNS error), so it is
		# retried, and the tag is never reported as pushed unless git says it was. If it still fails, the
		# local tag is removed, so a re-run starts clean instead of stopping at "Tag already exists".
		git tag $version
		$pushed = $false
		for ($attempt = 1; $attempt -le 3 -and -not $pushed; $attempt++) {
			git push origin $version
			if ($LASTEXITCODE -eq 0) {
				$pushed = $true
			}
			elseif ($attempt -lt 3) {
				Write-Warning "Pushing tag $version failed (attempt $attempt of 3); retrying."
				Start-Sleep -Seconds 5
			}
		}
		if (-not $pushed) {
			git tag -d $version | Out-Null
			Write-Error "Could not push tag $version to origin, so nothing was published. The local tag has been removed; re-run when the connection is back."
			exit 1
		}
		Write-Information "Tag $version pushed."

		if ($SkipPublishVerification) {
			Write-Warning "Not waiting for the release run (-SkipPublishVerification). Nothing has confirmed that a package reached nuget.org."
			exit 0
		}

		# The repository the run belongs to, read from the remote rather than assumed.
		$originUrl = git remote get-url origin
		$repoFullName = ($originUrl -replace '^.*github\.com[:/]', '') -replace '\.git$', ''

		Write-Information "Waiting for the release run for $version..."

		# The run takes a few seconds to appear after the tag push.
		$runId = $null
		for ($attempt = 1; $attempt -le 12 -and -not $runId; $attempt++) {
			Start-Sleep -Seconds 5
			# A tag push can start several workflows; only the release workflow's run is the one to watch.
			$runListJson = gh run list --repo $repoFullName --workflow ci.yml --branch $version --limit 1 --json databaseId 2>$null
			if ($LASTEXITCODE -eq 0 -and $runListJson) {
				$runList = $runListJson | ConvertFrom-Json
				if ($runList.Count -gt 0) { $runId = $runList[0].databaseId }
			}
		}

		if (-not $runId) {
			Write-Error "Tag $version was pushed but no run appeared for it. Check https://github.com/$repoFullName/actions: the workflow may not trigger on tags, or GitHub may be rate-limiting the lookups."
			exit 1
		}

		Write-Information "Run: https://github.com/$repoFullName/actions/runs/$runId"
		gh run watch $runId --repo $repoFullName --exit-status --interval 20
		$runExitCode = $LASTEXITCODE

		if ($runExitCode -ne 0) {
			Write-Information ""
			Write-Warning "The release run did not succeed: https://github.com/$repoFullName/actions/runs/$runId"

			# A refused job (an exhausted Actions budget, for instance) fails before any step runs, so it
			# has no failed step to report. The check-run annotation is the only place the reason appears.
			$jobId = gh api "repos/$repoFullName/actions/runs/$runId/jobs" --jq '.jobs[0].id' 2>$null
			if ($LASTEXITCODE -eq 0 -and $jobId) {
				$annotation = gh api "repos/$repoFullName/check-runs/$jobId/annotations" --jq '.[0].message' 2>$null
				if ($LASTEXITCODE -eq 0 -and $annotation) {
					Write-Warning "Reason: $annotation"
				}
			}

			Write-Information ""
			Write-Warning "Tag $version is pushed but no package was published. Once the cause is fixed:"
			Write-Information "  gh run rerun $runId --repo $repoFullName --failed"
			exit 1
		}

		Write-Information "Package $version published."
		""";
}
