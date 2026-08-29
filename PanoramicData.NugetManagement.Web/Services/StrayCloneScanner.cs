namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// An owner folder in the clone root that belongs to none of the organisations under management.
/// </summary>
/// <param name="Owner">The GitHub owner the folder is named for.</param>
/// <param name="Path">Where the folder is on disk.</param>
/// <param name="RepositoryNames">The repositories cloned inside it.</param>
public record StrayClone(string Owner, string Path, IReadOnlyList<string> RepositoryNames);

/// <summary>
/// Finds clones the app made of repositories belonging to somebody else.
/// </summary>
/// <remarks>
/// Clones are filed by owner, so a repository governed by mistake leaves an owner folder behind that
/// no organisation accounts for. Those folders hold real checkouts of other people's code and may
/// hold work, so they are reported and never deleted: the panel names them and the decision is the
/// user's.
/// </remarks>
public static class StrayCloneScanner
{
	/// <summary>
	/// Every owner folder under <paramref name="reposRoot"/> outside <paramref name="organizations"/>.
	/// </summary>
	public static IReadOnlyList<StrayClone> FindStrayClones(string reposRoot, IReadOnlyList<string> organizations)
	{
		if (!Directory.Exists(reposRoot))
		{
			// A clone root that has never been written to is nothing to report.
			return [];
		}

		return
		[
			.. Directory.EnumerateDirectories(reposRoot)
				.Select(path => new { Path = path, Owner = System.IO.Path.GetFileName(path) })
				.Where(candidate => !organizations.Contains(candidate.Owner, StringComparer.OrdinalIgnoreCase))
				.Select(candidate => new StrayClone(
					candidate.Owner,
					candidate.Path,
					[.. Directory.EnumerateDirectories(candidate.Path).Select(System.IO.Path.GetFileName).OfType<string>()]))
				.OrderBy(clone => clone.Owner, StringComparer.OrdinalIgnoreCase)
		];
	}
}
