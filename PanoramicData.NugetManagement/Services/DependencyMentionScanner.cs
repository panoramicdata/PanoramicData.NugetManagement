using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Whether a repository refers to a dependency anywhere it could be declaring one.
/// </summary>
/// <remarks>
/// The weak counterpart to <see cref="PackageReferenceScanner"/>. That one answers "what does this
/// repository depend on, and at what version?", and to answer it precisely it reads only the two
/// places a version can be read from — so a package referenced without a version, or declared in a
/// tool manifest, is invisible to it.
/// <para>
/// This one answers a different question: is the dependency here at all? It exists for the triage
/// verdict that closes a pull request because the repository no longer references what the pull
/// request proposes to move. Closing on the strength of "we could not find it" is only defensible if
/// finding it is made as easy as possible, so the match is deliberately loose — every file that could
/// plausibly declare a dependency, and a substring match within it. Both looseneses fail in the same
/// direction: a false sighting leaves a pull request open, which is the harmless outcome.
/// </para>
/// </remarks>
public static class DependencyMentionScanner
{
	/// <summary>
	/// Whether the repository mentions this dependency anywhere it could be declared.
	/// </summary>
	/// <param name="context">The repository.</param>
	/// <param name="dependency">The dependency to look for.</param>
	/// <remarks>
	/// A context listing no files at all answers yes to everything. Both builders return an empty file
	/// list when the read of the repository fails, and that is indistinguishable from a repository
	/// with nothing in it — so treating it as proof of absence would close every open Dependabot pull
	/// request in the estate the first time a clone went missing.
	/// <para>
	/// A declaration site whose content the context never fetched answers yes on its own. The context
	/// builders read content for a chosen set of files rather than the whole tree, so an unread
	/// declaration site is an ordinary state — and an unread file is not an empty one. Reporting the
	/// dependency absent because nobody looked in the one file that would have named it is precisely
	/// the mistake this whole scan exists to avoid making.
	/// </para>
	/// </remarks>
	public static bool Mentions(RepositoryContext context, DependencyRef dependency)
		=> context.FilePaths.Count == 0
			|| context.FilePaths
				.Where(path => IsDeclarationSite(path, dependency.Ecosystem))
				.Select(context.GetFileContent)
				.Any(content =>
					content is null
						|| content.Contains(dependency.Name, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Whether a file is one this ecosystem's dependencies can be declared in.
	/// </summary>
	/// <remarks>
	/// Read off <see cref="RepositoryContext.FilePaths"/> rather than
	/// <see cref="RepositoryContext.FindFiles"/>: that one drops the projects the assessment does not
	/// cover, and a dependency declared only by a sample project is still one the repository has.
	/// <para>
	/// Restricted to declaration sites rather than every file, so that a package named in a README or
	/// left behind in a <c>.gitignore</c> comment does not count as a sighting — that would hold a
	/// stale pull request open indefinitely, which is the failure this scan exists to end.
	/// </para>
	/// </remarks>
	private static bool IsDeclarationSite(string path, DependencyEcosystem ecosystem)
		=> ecosystem switch
		{
			DependencyEcosystem.NuGet =>
				EndsWithAny(path, ".csproj", ".fsproj", ".vbproj", ".props", ".targets")
					|| FileNameIs(path, "packages.config")
					|| FileNameIs(path, "dotnet-tools.json"),

			// Workflows and the composite actions they call: both carry `uses:` lines, and Dependabot
			// updates both.
			DependencyEcosystem.GitHubActions =>
				EndsWithAny(path, ".yml", ".yaml")
					&& (path.Contains(".github/workflows/", StringComparison.OrdinalIgnoreCase)
						|| FileNameIs(path, "action.yml")
						|| FileNameIs(path, "action.yaml")),

			_ => false
		};

	private static bool EndsWithAny(string path, params string[] suffixes)
		=> suffixes.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

	private static bool FileNameIs(string path, string fileName)
		=> path.Equals(fileName, StringComparison.OrdinalIgnoreCase)
			|| path.EndsWith('/' + fileName, StringComparison.OrdinalIgnoreCase)
			|| path.EndsWith('\\' + fileName, StringComparison.OrdinalIgnoreCase);
}
