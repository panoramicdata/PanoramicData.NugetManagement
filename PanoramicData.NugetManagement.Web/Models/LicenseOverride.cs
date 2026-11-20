namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// A licence set at one level: the application default, an organisation, or a repository.
/// </summary>
public sealed class LicenseOverride
{
	/// <summary>The SPDX licence expression, e.g. "LGPL-2.1-or-later"; blank inherits.</summary>
	public string? License { get; set; }

	/// <summary>The text LIC-01 looks for in LICENSE; blank derives it from the licence.</summary>
	public string? LicenseFileText { get; set; }

	/// <summary>Whether neither field is set, so the level inherits entirely.</summary>
	public bool IsEmpty => string.IsNullOrWhiteSpace(License) && string.IsNullOrWhiteSpace(LicenseFileText);

	/// <summary>A copy with blanks as null and values trimmed, or null when nothing is set.</summary>
	public LicenseOverride? Normalised()
		=> IsEmpty
			? null
			: new LicenseOverride
			{
				License = string.IsNullOrWhiteSpace(License) ? null : License.Trim(),
				LicenseFileText = string.IsNullOrWhiteSpace(LicenseFileText) ? null : LicenseFileText.Trim()
			};
}

/// <summary>
/// Where an effective licence came from.
/// </summary>
public enum LicenseSource
{
	/// <summary>appsettings.json or user secrets.</summary>
	Configuration,

	/// <summary>The default set on the Application settings page.</summary>
	Application,

	/// <summary>The repository's organisation.</summary>
	Organization,

	/// <summary>The repository itself.</summary>
	Repository
}

/// <summary>
/// The licence a repository is held to, and where it came from.
/// </summary>
/// <param name="License">The SPDX licence expression.</param>
/// <param name="LicenseFileText">The explicit LICENSE marker text, or null to derive it.</param>
/// <param name="Source">The level that supplied <paramref name="License"/>.</param>
public sealed record EffectiveLicense(string License, string? LicenseFileText, LicenseSource Source);
