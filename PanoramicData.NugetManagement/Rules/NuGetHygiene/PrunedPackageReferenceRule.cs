using System.Xml.Linq;
using NuGet.Versioning;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that no net11.0 project references a package the framework already supplies.
/// </summary>
public class PrunedPackageReferenceRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "PKG-15";

	/// <inheritdoc />
	public override string RuleName => "No references to packages the framework supplies";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.NuGetHygiene;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var centralVersions = ReadCentralVersions(context.GetFileContent("Directory.Packages.props"));
		var removals = new List<string>();

		foreach (var csproj in context.FindFiles(".csproj"))
		{
			var content = context.GetFileContent(csproj);
			if (content is null || !HasMsBuildProperty(content, "TargetFramework", Net11PrunedPackages.TargetFramework))
			{
				continue;
			}

			foreach (var (package, version) in ReadPackageReferences(content))
			{
				if (!Net11PrunedPackages.Versions.TryGetValue(package, out var supplied))
				{
					continue;
				}

				var referenced = version ?? (centralVersions.TryGetValue(package, out var central) ? central : null);
				if (IsPruned(referenced, supplied))
				{
					removals.Add($"{csproj}|{package}");
				}
			}
		}

		if (removals.Count == 0)
		{
			return Task.FromResult(Pass($"No {Net11PrunedPackages.TargetFramework} project references a package the framework supplies."));
		}

		var list = string.Join(", ", removals.Select(r => $"`{r.Replace("|", " → ", StringComparison.Ordinal)}`"));
		return Task.FromResult(Fail(
			$"These references are supplied by {Net11PrunedPackages.TargetFramework} and fail the build with NU1510: {list}.",
			new RuleAdvisory
			{
				Summary = $"Remove the PackageReference to each package {Net11PrunedPackages.TargetFramework} already supplies.",
				Detail = $"NuGet prunes any package the target framework carries at the same or a newer version, and reports NU1510, an error under TreatWarningsAsErrors. Remove: {list}.",
				Data = new()
				{
					["remediation_type"] = "remove_packagereferences",
					["removals"] = removals.ToArray()
				}
			}));
	}

	/// <summary>
	/// A reference is pruned when its version is at or below the one the framework carries. A reference
	/// with no resolvable version is left alone: removing it would be a guess.
	/// </summary>
	private static bool IsPruned(string? referenced, string supplied)
		=> referenced is not null
			&& VersionRange.TryParse(referenced, out var range)
			&& range.MinVersion is { } min
			&& NuGetVersion.TryParse(supplied, out var carried)
			&& min <= carried;

	private static IEnumerable<(string Package, string? Version)> ReadPackageReferences(string xml)
	{
		XDocument document;
		try
		{
			document = XDocument.Parse(xml);
		}
		catch (System.Xml.XmlException)
		{
			yield break;
		}

		foreach (var reference in document.Descendants("PackageReference"))
		{
			var name = reference.Attribute("Include")?.Value;
			if (!string.IsNullOrEmpty(name))
			{
				yield return (name, reference.Attribute("Version")?.Value ?? reference.Element("Version")?.Value);
			}
		}
	}

	private static Dictionary<string, string> ReadCentralVersions(string? xml)
	{
		var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (xml is null)
		{
			return versions;
		}

		try
		{
			foreach (var entry in XDocument.Parse(xml).Descendants("PackageVersion"))
			{
				var name = entry.Attribute("Include")?.Value;
				var version = entry.Attribute("Version")?.Value;
				if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(version))
				{
					versions[name] = version;
				}
			}
		}
		catch (System.Xml.XmlException)
		{
			// An unparseable props file means no central version is known, so nothing is removed on its account.
		}

		return versions;
	}
}
