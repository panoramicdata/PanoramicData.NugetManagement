using System.Xml;
using System.Xml.Linq;

namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// Context provided to rules for evaluating a repository.
/// Contains pre-fetched file contents from the repository.
/// </summary>
public class RepositoryContext
{
	/// <summary>
	/// The GitHub repository full name (e.g. "panoramicdata/Highlight.Api").
	/// </summary>
	public required string FullName { get; init; }

	/// <summary>
	/// The repository name (e.g. "Highlight.Api").
	/// </summary>
	public required string Name { get; init; }

	/// <summary>
	/// The default branch name.
	/// </summary>
	public required string DefaultBranch { get; init; }

	/// <summary>
	/// The currently checked out local branch when available.
	/// Null when context is built from remote-only repository metadata.
	/// </summary>
	public string? CurrentBranch { get; init; }

	/// <summary>
	/// The per-repo options (may be default).
	/// </summary>
	public required RepoOptions Options { get; init; }

	/// <summary>
	/// All file paths in the repository (relative).
	/// </summary>
	public required List<string> FilePaths { get; init; }

	/// <summary>
	/// Pre-fetched file contents keyed by relative path.
	/// Not all files will be fetched — only those relevant to assessment.
	/// </summary>
	public required Dictionary<string, string> FileContents { get; init; }

	/// <summary>
	/// Optional repository-level project treatment configuration.
	/// </summary>
	public NugetManagementRepositoryConfig? RepositoryConfig { get; init; }

	/// <summary>
	/// The newest version tag on the repository, or null when it is unknown — the repository is not
	/// cloned locally, or has never been tagged.
	/// </summary>
	public string? LatestTag { get; init; }

	/// <summary>
	/// The newest version of this repository's package on nuget.org, or null when nothing has been
	/// published or the feed could not be reached.
	/// </summary>
	public string? LatestPublishedVersion { get; init; }

	/// <summary>
	/// Measured line coverage as a percentage, or null when this repository's tests have not been run
	/// with coverage collection.
	/// </summary>
	public double? LineCoveragePercent { get; init; }

	/// <summary>
	/// Measured branch coverage as a percentage, or null when it has not been measured.
	/// </summary>
	public double? BranchCoveragePercent { get; init; }

	/// <summary>
	/// Gets the content of a file, or null if not fetched.
	/// </summary>
	/// <param name="path">The relative file path.</param>
	/// <returns>The file content, or null if not present.</returns>
	public string? GetFileContent(string path)
		=> FileContents.TryGetValue(path, out var content) ? content : null;

	/// <summary>
	/// Whether a file exists in the repository.
	/// </summary>
	/// <param name="path">The relative file path.</param>
	/// <returns>True if the file path exists in the repository tree.</returns>
	public bool FileExists(string path)
		=> FilePaths.Contains(path, StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Finds all file paths matching a pattern (case-insensitive).
	/// </summary>
	/// <param name="suffix">The suffix to match (e.g. ".csproj").</param>
	/// <returns>The matching file paths.</returns>
	public IEnumerable<string> FindFiles(string suffix)
	{
		var matching = FilePaths.Where(p => p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
		if (!suffix.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
		{
			return matching;
		}

		return matching.Where(ShouldIncludeProjectInAssessment);
	}

	/// <summary>
	/// Gets configured test projects after applying heuristics and project overrides.
	/// </summary>
	public IEnumerable<string> FindTestProjectFiles()
		=> FilePaths
			.Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
			.Where(ShouldIncludeProjectInTesting);

	/// <summary>
	/// Gets non-test projects after applying project overrides.
	/// </summary>
	public IEnumerable<string> FindNonTestProjectFiles()
		=> FindFiles(".csproj").Where(path => !IsTestProject(path));

	/// <summary>
	/// Returns every project this repository publishes to NuGet.
	/// </summary>
	/// <remarks>
	/// Resolved by what a project says about itself, not by what it is called. Matching a .csproj to
	/// the repository name looked reasonable and was wrong in both directions: repositories whose
	/// package project is named something else had every packaging rule silently skipped, while the
	/// same repositories had their real package reported as an ancillary project that ought to be
	/// non-packable. A repository may publish several packages, so this returns all of them.
	/// </remarks>
	public IEnumerable<string> FindPackableProjectFiles()
		=> FindNonTestProjectFiles().Where(IsPackableProject);

	/// <summary>
	/// Returns the non-test projects this repository does not publish: tools, samples, generators and
	/// the like, which should be explicitly opted out of packaging.
	/// </summary>
	public IEnumerable<string> FindNonPackableProjectFiles()
		=> FindNonTestProjectFiles().Where(path => !IsPackableProject(path));

	/// <summary>
	/// Whether a project is one the repository publishes.
	/// </summary>
	/// <param name="projectPath">The relative project path.</param>
	public bool IsPackableProject(string projectPath)
	{
		var treatment = GetProjectConfig(projectPath)?.PackagingTreatment ?? ProjectTreatment.Auto;
		if (treatment != ProjectTreatment.Auto)
		{
			return treatment == ProjectTreatment.Include;
		}

		var content = GetFileContent(projectPath);

		// An explicit opt-out settles it, whatever else the project says.
		if (MsBuildProperties.HasValue(content, "IsPackable", "false"))
		{
			return false;
		}

		return MsBuildProperties.Has(content, "PackageId")
			|| MsBuildProperties.HasValue(content, "GeneratePackageOnBuild", "true")
			|| MsBuildProperties.HasValue(content, "PackAsTool", "true")
			|| MsBuildProperties.HasValue(content, "IsPackable", "true");
	}

	/// <summary>
	/// Whether a project is a Roslyn compiler extension: an analyzer or source generator loaded
	/// in-process by the compiler.
	/// </summary>
	/// <remarks>
	/// These projects must target netstandard2.0, because the compiler host is .NET Framework under
	/// Visual Studio and MSBuild.exe, and an assembly built for a newer target framework cannot load
	/// into it — which is what RS1041 enforces. Rules that would otherwise push a project onto the
	/// latest target framework have to leave them alone: doing so to Meraki.Api's source generator
	/// broke the build outright, and in Visual Studio the generator simply stops loading, with no
	/// diagnostic pointing at the cause.
	/// </remarks>
	/// <param name="projectPath">The relative project path.</param>
	public bool IsCompilerExtensionProject(string projectPath)
	{
		var content = GetFileContent(projectPath);

		// The conventional marker, and the cheapest signal.
		if (MsBuildProperties.HasValue(content, "EnforceExtendedAnalyzerRules", "true"))
		{
			return true;
		}

		// A Roslyn dependency on its own is not enough — a tool or a test project may reference
		// Microsoft.CodeAnalysis and still belong on the latest framework. Paired with netstandard2.0
		// it is a project that has already made the compiler-extension choice deliberately.
		if (MsBuildProperties.HasValue(content, "TargetFramework", "netstandard2.0")
			&& ReferencesRoslyn(content))
		{
			return true;
		}

		return IsConsumedAsAnalyzer(projectPath);
	}

	/// <summary>
	/// Gets the project-level config for a path, if any.
	/// </summary>
	public NugetManagementProjectConfig? GetProjectConfig(string projectPath)
		=> RepositoryConfig?.GetProjectConfig(projectPath);

	/// <summary>
	/// Determines whether a project should be treated as a test project.
	/// </summary>
	public bool IsTestProject(string projectPath)
	{
		var projectConfig = GetProjectConfig(projectPath);
		return projectConfig?.TestingTreatment switch
		{
			ProjectTreatment.Include => true,
			ProjectTreatment.Exclude => false,
			_ => IsLikelyTestProject(projectPath)
		};
	}

	private bool ShouldIncludeProjectInAssessment(string projectPath)
	{
		var projectConfig = GetProjectConfig(projectPath);
		return projectConfig?.Treatment switch
		{
			ProjectTreatment.Include => true,
			ProjectTreatment.Exclude => false,
			_ => IsAutoIncludedProject(projectPath)
		};
	}

	private bool ShouldIncludeProjectInTesting(string projectPath)
	{
		var projectConfig = GetProjectConfig(projectPath);

		if (projectConfig?.TestingTreatment == ProjectTreatment.Include)
		{
			return true;
		}

		if (projectConfig?.TestingTreatment == ProjectTreatment.Exclude)
		{
			return false;
		}

		if (projectConfig?.Treatment == ProjectTreatment.Exclude)
		{
			return false;
		}

		return IsLikelyTestProject(projectPath) && IsAutoIncludedProject(projectPath);
	}

	private static bool IsLikelyTestProject(string projectPath)
	{
		var fileName = Path.GetFileName(projectPath);
		return fileName.Contains(".Test", StringComparison.OrdinalIgnoreCase)
			|| fileName.Contains(".Tests", StringComparison.OrdinalIgnoreCase)
			|| fileName.EndsWith("Tests.csproj", StringComparison.OrdinalIgnoreCase)
			|| fileName.EndsWith("Test.csproj", StringComparison.OrdinalIgnoreCase);
	}

	private static bool ReferencesRoslyn(string? content)
	{
		if (string.IsNullOrWhiteSpace(content))
		{
			return false;
		}

		try
		{
			return XDocument.Parse(content)
				.Descendants()
				.Where(element => string.Equals(element.Name.LocalName, "PackageReference", StringComparison.OrdinalIgnoreCase))
				.Select(element => element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value)
				.Any(id => id is not null
					&& (id.Trim().StartsWith("Microsoft.CodeAnalysis.", StringComparison.OrdinalIgnoreCase)
						|| string.Equals(id.Trim(), "Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase)));
		}
		catch (XmlException)
		{
			return false;
		}
	}

	/// <summary>
	/// Whether any other project in the repository consumes this one as an analyzer, i.e. with
	/// OutputItemType="Analyzer" on its ProjectReference. That is how a generator is wired in, and it
	/// is stated by the consumer rather than by the generator itself.
	/// </summary>
	private bool IsConsumedAsAnalyzer(string projectPath)
	{
		var normalisedTarget = NormalisePath(projectPath);

		foreach (var candidate in FilePaths.Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
		{
			var content = GetFileContent(candidate);
			if (string.IsNullOrWhiteSpace(content))
			{
				continue;
			}

			List<string?> analyzerReferences;
			try
			{
				analyzerReferences = [.. XDocument.Parse(content)
					.Descendants()
					.Where(element => string.Equals(element.Name.LocalName, "ProjectReference", StringComparison.OrdinalIgnoreCase))
					.Where(element => string.Equals(
						element.Attribute("OutputItemType")?.Value?.Trim(),
						"Analyzer",
						StringComparison.OrdinalIgnoreCase))
					.Select(element => element.Attribute("Include")?.Value)];
			}
			catch (XmlException)
			{
				continue;
			}

			var candidateDirectory = Path.GetDirectoryName(NormalisePath(candidate)) ?? string.Empty;

			foreach (var include in analyzerReferences)
			{
				if (string.IsNullOrWhiteSpace(include))
				{
					continue;
				}

				var resolved = ResolveRelativeTo(candidateDirectory, NormalisePath(include.Trim()));
				if (string.Equals(resolved, normalisedTarget, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
		}

		return false;
	}

	private static string NormalisePath(string path) => path.Replace('\\', '/').TrimStart('/');

	/// <summary>
	/// Resolves a project-relative path (which may contain "..") against the directory of the project
	/// that declared it, producing a repository-relative path.
	/// </summary>
	private static string ResolveRelativeTo(string directory, string relativePath)
	{
		var segments = new List<string>();

		foreach (var segment in $"{directory}/{relativePath}".Split('/', StringSplitOptions.RemoveEmptyEntries))
		{
			if (segment == ".")
			{
				continue;
			}

			if (segment == "..")
			{
				if (segments.Count > 0)
				{
					segments.RemoveAt(segments.Count - 1);
				}

				continue;
			}

			segments.Add(segment);
		}

		return string.Join('/', segments);
	}

	private static bool IsAutoIncludedProject(string projectPath)
		=> !projectPath.Contains("/Fixtures/", StringComparison.OrdinalIgnoreCase)
			&& !projectPath.Contains("/TestData/", StringComparison.OrdinalIgnoreCase)
			&& !projectPath.Contains("/Samples/", StringComparison.OrdinalIgnoreCase);
}
