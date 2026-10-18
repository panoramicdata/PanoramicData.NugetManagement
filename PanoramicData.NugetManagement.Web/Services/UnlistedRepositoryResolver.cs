using System.Collections.Concurrent;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// What GitHub says a repository is called and what state it is in now.
/// </summary>
/// <param name="FullName">The repository's current <c>owner/name</c>, which differs from the one asked for after a rename.</param>
/// <param name="IsArchived">Whether it is archived (read-only).</param>
/// <param name="IsFork">Whether it is a fork.</param>
public sealed record ResolvedRepository(string FullName, bool IsArchived, bool IsFork);

/// <summary>
/// Finds out what became of repositories that packages still point at but that an organisation's repository
/// list does not show under that name.
/// </summary>
/// <remarks>
/// A package records its repository in its nuspec, and nothing updates that when the repository is renamed.
/// Archiving by renaming (a <c>.Archived</c> suffix) is exactly how that happens here, and the organisation's
/// list then shows the new name while the package still says the old one. Matching the two by full name never
/// finds the pair, so the repository's archived state was never learned and it stayed governed: it was
/// assessed, committed to, and pushed to, and the push was refused with a 403.
/// <para>
/// GitHub follows a rename when asked for the old name, so asking for each unmatched name settles it. The
/// answer is returned under the name the package records, because that is the name rows are keyed by.
/// </para>
/// </remarks>
public static class UnlistedRepositoryResolver
{
	/// <summary>How many lookups run at once; the unmatched set is small, so this is a courtesy to the API.</summary>
	private const int MaxConcurrentLookups = 4;

	/// <summary>
	/// Looks up every repository a package points at in this organisation that the list does not contain.
	/// </summary>
	/// <param name="organization">The organisation whose list was read.</param>
	/// <param name="published">The <c>owner/name</c> of every repository a package points at.</param>
	/// <param name="listed">What GitHub listed for the organisation.</param>
	/// <param name="lookup">Asks GitHub about one repository by <c>owner/name</c>; null when it does not exist.</param>
	/// <param name="logger">Where a repository that moved, or a lookup that failed, is reported.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>
	/// One entry per repository found, named as the package records it. A repository that is gone, that now
	/// belongs to another owner, or whose lookup failed, is left out: it stays as it was rather than being
	/// guessed about.
	/// </returns>
	public static async Task<IReadOnlyList<GitHubRepositoryInfo>> ResolveAsync(
		string organization,
		IReadOnlySet<string> published,
		IReadOnlyCollection<GitHubRepositoryInfo> listed,
		Func<string, CancellationToken, Task<ResolvedRepository?>> lookup,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		var found = new ConcurrentBag<GitHubRepositoryInfo>();

		await Parallel.ForEachAsync(
			UnlistedNames(organization, published, listed),
			new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentLookups, CancellationToken = cancellationToken },
			async (recordedName, ct) =>
			{
				if (await DescribeAsync(organization, recordedName, lookup, logger, ct).ConfigureAwait(false) is { } info)
				{
					found.Add(info);
				}
			}).ConfigureAwait(false);

		return [.. found.OrderBy(repository => repository.FullName, StringComparer.OrdinalIgnoreCase)];
	}

	private static List<string> UnlistedNames(
		string organization,
		IReadOnlySet<string> published,
		IReadOnlyCollection<GitHubRepositoryInfo> listed)
	{
		var listedNames = new HashSet<string>(listed.Select(repository => repository.FullName), StringComparer.OrdinalIgnoreCase);

		return
		[
			.. published
				.Where(name => string.Equals(OwnerOf(name), organization, StringComparison.OrdinalIgnoreCase))
				.Where(name => !listedNames.Contains(name))
				.Order(StringComparer.OrdinalIgnoreCase)
		];
	}

	/// <summary>
	/// What GitHub says about one repository, as an entry named the way the package names it, or null when
	/// there is nothing trustworthy to report.
	/// </summary>
	private static async Task<GitHubRepositoryInfo?> DescribeAsync(
		string organization,
		string recordedName,
		Func<string, CancellationToken, Task<ResolvedRepository?>> lookup,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		ResolvedRepository? resolved;

		try
		{
			resolved = await lookup(recordedName, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Not knowing is not a reason to change what we believed: it stays governed, as before.
			logger.LogWarning(ex, "Could not look up {Repository}, so whether it is archived is unknown.", recordedName);
			return null;
		}

		if (resolved is null)
		{
			return null;
		}

		// A rename keeps the owner. A transfer does not, and then the repository is not this organisation's to
		// describe: its archived state belongs to somebody else's repository and must not be attached to a row
		// governed here.
		if (!string.Equals(OwnerOf(resolved.FullName), organization, StringComparison.OrdinalIgnoreCase))
		{
			logger.LogWarning(
				"{Recorded} now belongs to {Current}, outside {Organization}, so it is left as it was.",
				recordedName,
				resolved.FullName,
				organization);

			return null;
		}

		if (!string.Equals(resolved.FullName, recordedName, StringComparison.OrdinalIgnoreCase))
		{
			logger.LogInformation(
				"{Recorded} has moved to {Current} (archived: {Archived}).",
				recordedName,
				resolved.FullName,
				resolved.IsArchived);
		}

		return new GitHubRepositoryInfo(
			recordedName,
			$"https://github.com/{resolved.FullName}",
			resolved.IsArchived,
			resolved.IsFork);
	}

	private static string OwnerOf(string fullName)
	{
		var slash = fullName.IndexOf('/', StringComparison.Ordinal);

		return slash < 0 ? string.Empty : fullName[..slash];
	}
}
