using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Octokit;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Reads the NuGet package ids a repository declares in its project files, which is what makes a
/// repository that has never published anything one of ours to govern.
/// </summary>
/// <remarks>
/// A package id lives in a <c>.csproj</c>, or in a <c>Directory.Build.props</c> that every project
/// inherits. A solution file (<c>.slnx</c>) only lists projects, so it is never consulted.
/// </remarks>
public static partial class DeclaredPackageIds
{
	/// <summary>
	/// Whether a file in a repository's tree is one a package id can be declared in.
	/// </summary>
	/// <param name="path">The file's path within the repository.</param>
	public static bool IsCandidate(string path)
		=> path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(Path.GetFileName(path), "Directory.Build.props", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// The package ids a project file declares, in the order it declares them.
	/// </summary>
	/// <param name="projectXml">The project file's contents.</param>
	public static IEnumerable<string> In(string projectXml)
		=> PackageIdElement()
			.Matches(projectXml)
			.Select(match => match.Groups["id"].Value.Trim())
			.Where(id => id.Length > 0);

	/// <summary>
	/// Reads the package ids declared on a repository's default branch.
	/// </summary>
	/// <param name="github">The GitHub client.</param>
	/// <param name="owner">The repository's owner.</param>
	/// <param name="name">The repository's name.</param>
	/// <param name="branch">The branch to read, normally the default branch.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>The distinct package ids declared; empty when the repository declares none or is empty.</returns>
	/// <remarks>
	/// Stops at the first file that declares one: the question is whether the repository is a package
	/// repository, and one id answers it without fetching every project in a large solution.
	/// Any failure other than an empty or missing repository propagates, so the caller can tell
	/// "declares nothing" from "could not tell".
	/// </remarks>
	public static async Task<IReadOnlyList<string>> ReadAsync(
		IGitHubClient github,
		string owner,
		string name,
		string branch,
		CancellationToken cancellationToken)
	{
		TreeResponse tree;
		try
		{
			tree = await github.Git.Tree
				.GetRecursive(owner, name, branch)
				.WaitAsync(cancellationToken)
				.ConfigureAwait(false);
		}
		catch (NotFoundException)
		{
			return [];
		}
		catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
		{
			// GitHub answers 409 for a repository with no commits at all.
			return [];
		}

		var candidates = tree.Tree
			.Where(item => item.Type.Value == TreeType.Blob && IsCandidate(item.Path))
			.Select(item => item.Path)
			.OrderBy(path => path.Count(c => c == '/'))
			.ThenBy(path => path, StringComparer.OrdinalIgnoreCase);

		foreach (var path in candidates)
		{
			var content = await github.Repository.Content
				.GetRawContentByRef(owner, name, path, branch)
				.WaitAsync(cancellationToken)
				.ConfigureAwait(false);

			var ids = In(Encoding.UTF8.GetString(content))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();

			if (ids.Count > 0)
			{
				return ids;
			}
		}

		return [];
	}

	[GeneratedRegex(@"<PackageId(?:\s[^>]*)?>(?<id>[^<]*)</PackageId>", RegexOptions.IgnoreCase)]
	private static partial Regex PackageIdElement();
}
