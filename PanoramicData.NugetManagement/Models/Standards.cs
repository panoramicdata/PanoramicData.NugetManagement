namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// Standard file contents and values that all repositories should conform to.
/// These are the opinionated constants maintained in this package.
/// </summary>
public static class Standards
{
	/// <summary>
	/// The latest .NET target framework moniker.
	/// </summary>
	public const string LatestTargetFramework = "net10.0";

	/// <summary>
	/// The latest .NET SDK version for global.json.
	/// </summary>
	public const string LatestDotNetSdkVersion = "10.0.100";

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
}
