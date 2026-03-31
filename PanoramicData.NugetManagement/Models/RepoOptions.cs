namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// Per-repository options that override default assessment behaviour.
/// </summary>
public class RepoOptions
{
	/// <summary>
	/// Whether this repository should be excluded from assessment entirely.
	/// </summary>
	public bool Exclude { get; set; }

	/// <summary>
	/// Whether this repository is a NuGet-packable library (vs. an app, tool, etc.).
	/// When false, NuGet packaging rules (snupkg, PackageId, etc.) are skipped.
	/// </summary>
	public bool IsPackable { get; set; } = true;

	/// <summary>
	/// Whether to enforce the 'required' keyword on DTO properties.
	/// Some repos have DTOs where the remote API does not guarantee all fields.
	/// </summary>
	public bool EnforceRequiredProperties { get; set; } = true;

	/// <summary>
	/// Rule IDs to suppress for this repository.
	/// </summary>
	public List<string> SuppressedRules { get; set; } = [];

	/// <summary>
	/// The expected SPDX license expression (e.g. "MIT", "Apache-2.0").
	/// Defaults to <see cref="Standards.LicenseType"/>.
	/// </summary>
	public string ExpectedLicense { get; set; } = Standards.LicenseType;

	/// <summary>
	/// The text expected to appear in the LICENSE file to confirm the license type.
	/// Defaults to "{ExpectedLicense} License" (e.g. "MIT License").
	/// When null, uses "{ExpectedLicense} License".
	/// </summary>
	public string? ExpectedLicenseFileText { get; set; }

	/// <summary>
	/// The expected copyright holder name.
	/// Defaults to <see cref="Standards.CopyrightHolder"/>.
	/// </summary>
	public string ExpectedCopyrightHolder { get; set; } = Standards.CopyrightHolder;

	/// <summary>
	/// The expected HTTP client package name (e.g. "Refit").
	/// Defaults to <see cref="Standards.ExpectedHttpClientPackage"/>.
	/// </summary>
	public string ExpectedHttpClientPackage { get; set; } = Standards.ExpectedHttpClientPackage;

	/// <summary>
	/// Gets the text that should appear in the LICENSE file.
	/// </summary>
	/// <returns>The expected license file text.</returns>
	public string GetExpectedLicenseFileText()
		=> ExpectedLicenseFileText ?? $"{ExpectedLicense} License";
}
