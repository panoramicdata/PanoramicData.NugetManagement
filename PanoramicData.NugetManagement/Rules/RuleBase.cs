using System.Xml;
using System.Xml.Linq;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Base class for rules, providing common helper methods.
/// </summary>
public abstract class RuleBase : IRule
{
	/// <inheritdoc />
	public abstract string RuleId { get; }

	/// <inheritdoc />
	public abstract string RuleName { get; }

	/// <inheritdoc />
	public abstract AssessmentCategory Category { get; }

	/// <inheritdoc />
	public abstract AssessmentSeverity Severity { get; }

	/// <inheritdoc />
	public abstract Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken);

	/// <summary>
	/// Creates a passing result for this rule.
	/// </summary>
	/// <param name="message">The success message.</param>
	/// <returns>A passing RuleResult.</returns>
	protected RuleResult Pass(string message) => new()
	{
		RuleId = RuleId,
		RuleName = RuleName,
		Category = Category,
		Severity = Severity,
		Passed = true,
		Message = message
	};

	/// <summary>
	/// Creates a not-applicable result for this rule: the rule was evaluated but is not relevant to
	/// this repository. Reported as passing so it never counts as a failure, but flagged via
	/// <see cref="RuleResult.IsApplicable"/> so "irrelevant" can be told apart from "compliant".
	/// </summary>
	/// <param name="message">Explanation of why the rule does not apply.</param>
	/// <returns>A passing, not-applicable RuleResult.</returns>
	protected RuleResult NotApplicable(string message) => new()
	{
		RuleId = RuleId,
		RuleName = RuleName,
		Category = Category,
		Severity = Severity,
		Passed = true,
		IsApplicable = false,
		Message = message
	};

	/// <summary>
	/// Creates a failing result for this rule with structured advisory data.
	/// </summary>
	/// <param name="message">The failure message.</param>
	/// <param name="advisory">Structured advisory for AI-driven remediation.</param>
	/// <returns>A failing RuleResult.</returns>
	protected RuleResult Fail(string message, RuleAdvisory advisory) => new()
	{
		RuleId = RuleId,
		RuleName = RuleName,
		Category = Category,
		Severity = Severity,
		Passed = false,
		Message = message,
#pragma warning disable CS0618 // Type or member is obsolete
		Remediation = advisory.Summary,
#pragma warning restore CS0618 // Type or member is obsolete
		Advisory = advisory
	};

	/// <summary>
	/// Creates a failing result for this rule.
	/// </summary>
	/// <param name="message">The failure message.</param>
	/// <param name="remediation">Optional remediation guidance.</param>
	/// <returns>A failing RuleResult.</returns>
	[Obsolete("Use Fail(string, RuleAdvisory) instead.")]
	protected RuleResult Fail(string message, string? remediation = null) => new()
	{
		RuleId = RuleId,
		RuleName = RuleName,
		Category = Category,
		Severity = Severity,
		Passed = false,
		Message = message,
#pragma warning disable CS0618 // Type or member is obsolete
		Remediation = remediation
#pragma warning restore CS0618 // Type or member is obsolete
	};

	/// <summary>
	/// Checks whether a file content contains a specific string.
	/// </summary>
	/// <param name="content">The file content to search.</param>
	/// <param name="search">The string to search for.</param>
	/// <returns>True if found.</returns>
	protected static bool Contains(string? content, string search)
		=> content?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

	/// <summary>
	/// Checks whether a project file is explicitly marked as non-packable.
	/// </summary>
	/// <param name="content">The .csproj file content.</param>
	/// <returns>True if the project contains &lt;IsPackable&gt;false&lt;/IsPackable&gt;.</returns>
	protected static bool IsExplicitlyNonPackable(string? content)
		=> Contains(content, "<IsPackable>false</IsPackable>");

	/// <summary>
	/// The MSBuild elements that declare a NuGet package dependency.
	/// </summary>
	private static readonly string[] _packageElementNames =
		["PackageReference", "PackageVersion", "GlobalPackageReference"];

	/// <summary>
	/// The MSBuild elements that declare a project's own choice to use a package. Excludes
	/// PackageVersion, which only pins a version and may exist purely to control a transitive
	/// dependency the repository never asked for.
	/// </summary>
	private static readonly string[] _directReferenceElementNames =
		["PackageReference", "GlobalPackageReference"];

	/// <summary>
	/// Checks whether a project or props file actually declares a reference to the given package.
	/// Unlike a raw substring search this ignores XML comments (including commented-out references)
	/// and text that merely mentions the package name, matching only the package identifier of a
	/// PackageReference / PackageVersion / GlobalPackageReference element.
	/// </summary>
	/// <param name="xml">The .csproj or .props file content.</param>
	/// <param name="packageId">The package identifier to look for.</param>
	/// <param name="includeVariants">
	/// When true, also matches sub-packages of the same family — e.g. a <paramref name="packageId"/>
	/// of "Refit" matches "Refit.HttpClientFactory", and "Flurl" matches "Flurl.Http".
	/// </param>
	/// <returns>True if the package is referenced.</returns>
	protected static bool ReferencesPackage(string? xml, string packageId, bool includeVariants = false)
		=> ReferencesPackageCore(xml, packageId, includeVariants, _packageElementNames);

	/// <summary>
	/// Checks whether a project declares its own reference to the given package. Unlike
	/// <see cref="ReferencesPackage"/>, a central PackageVersion pin does not count: pinning a
	/// version is not the same as choosing to depend on a package, since a pin may exist only to
	/// control the version of a transitive dependency imposed by something else.
	/// </summary>
	/// <param name="xml">The .csproj or .props file content.</param>
	/// <param name="packageId">The package identifier to look for.</param>
	/// <param name="includeVariants">Whether to also match sub-packages of the same family.</param>
	/// <returns>True if the package is referenced directly.</returns>
	protected static bool ReferencesPackageDirectly(string? xml, string packageId, bool includeVariants = false)
		=> ReferencesPackageCore(xml, packageId, includeVariants, _directReferenceElementNames);

	private static bool ReferencesPackageCore(string? xml, string packageId, bool includeVariants, string[] elementNames)
	{
		if (string.IsNullOrWhiteSpace(xml) || string.IsNullOrWhiteSpace(packageId))
		{
			return false;
		}

		var referencedIds = TryGetReferencedPackageIds(xml, elementNames);
		if (referencedIds is null)
		{
			// Not parseable as XML — fall back to a substring match so behaviour never degrades
			// below the previous best-effort check.
			return Contains(xml, packageId);
		}

		return referencedIds.Any(id =>
			string.Equals(id, packageId, StringComparison.OrdinalIgnoreCase)
			|| (includeVariants && id.StartsWith($"{packageId}.", StringComparison.OrdinalIgnoreCase)));
	}

	/// <summary>
	/// Returns the package identifiers declared in the given MSBuild XML, or null if the content
	/// could not be parsed as XML.
	/// </summary>
	private static List<string>? TryGetReferencedPackageIds(string xml, string[] elementNames)
	{
		try
		{
			return [.. XDocument.Parse(xml)
				.Descendants()
				.Where(element => elementNames.Contains(element.Name.LocalName, StringComparer.OrdinalIgnoreCase))
				.Select(element => element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value)
				.Where(id => !string.IsNullOrWhiteSpace(id))
				.Select(id => id!.Trim())];
		}
		catch (XmlException)
		{
			return null;
		}
	}
}
