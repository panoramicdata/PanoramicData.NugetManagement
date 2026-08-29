using System.Xml.Linq;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// A NuGet package version declared by a repository, and where it was declared.
/// </summary>
/// <param name="FilePath">The file the declaration was found in.</param>
/// <param name="PackageId">The package identifier.</param>
/// <param name="CurrentVersion">The declared version.</param>
/// <param name="VersionKind">Which element and version syntax declared it, for remediation.</param>
public sealed record PackageVersionReference(
	string FilePath,
	string PackageId,
	string CurrentVersion,
	string VersionKind);

/// <summary>
/// Finds every explicitly versioned NuGet package a repository declares, across central package
/// management and individual project files.
/// </summary>
/// <remarks>
/// Shared by the package freshness rules (PKG-07/08/09) and the deprecated dependency rule (PKG-12),
/// which all need the same answer to "what does this repository depend on, and at what version?".
/// </remarks>
public static class PackageReferenceScanner
{
	/// <summary>
	/// Scans a repository for every declared package version.
	/// </summary>
	/// <param name="context">The repository to scan.</param>
	/// <returns>Every explicitly versioned package declaration found.</returns>
	public static List<PackageVersionReference> Scan(RepositoryContext context)
	{
		var references = new List<PackageVersionReference>();

		AddDeclarations(
			context.GetFileContent("Directory.Packages.props"),
			"Directory.Packages.props",
			"PackageVersion",
			references);

		foreach (var projectPath in context.FindFiles(".csproj"))
		{
			AddDeclarations(context.GetFileContent(projectPath), projectPath, "PackageReference", references);
		}

		return references;
	}

	private static void AddDeclarations(
		string? content,
		string filePath,
		string elementName,
		List<PackageVersionReference> references)
	{
		if (string.IsNullOrWhiteSpace(content))
		{
			return;
		}

		try
		{
			foreach (var element in XDocument.Parse(content).Descendants(elementName))
			{
				var packageId = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;
				if (string.IsNullOrWhiteSpace(packageId))
				{
					continue;
				}

				var versionAttribute = element.Attribute("Version")?.Value;
				var currentVersion = versionAttribute ?? element.Element("Version")?.Value;
				if (string.IsNullOrWhiteSpace(currentVersion))
				{
					continue;
				}

				references.Add(new PackageVersionReference(
					filePath,
					packageId,
					currentVersion,
					versionAttribute is not null ? $"{elementName}Attribute" : $"{elementName}Element"));
			}
		}
		catch
		{
			// A project that does not parse as XML has nothing to contribute; other files still do.
		}
	}
}
