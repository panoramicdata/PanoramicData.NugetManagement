using System.Globalization;
using Microsoft.Extensions.Options;
using Octokit;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Web.Services;

public partial class DashboardService
{
	/// <summary>
	/// Discovers all packages and builds initial dashboard rows.
	/// </summary>
	/// <param name="organization">
	/// When supplied, only this organisation is discovered, so rediscovering one organisation does not
	/// pay the cost of every other. When null, every configured organisation is discovered.
	/// </param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <param name="github">
	/// A client for asking GitHub what each declared repository is really called. When null the names
	/// packages declare are taken at their word, which is what discovery did before.
	/// </param>
	public async Task<List<RepositoryDashboardRow>> DiscoverPackagesAsync(
		string? organization,
		CancellationToken cancellationToken,
		IGitHubClient? github)
	{
		var organizations = organization is null
			? _runtimeSettings.Organizations
			: [organization];

		var packages = await DiscoverOrganizationsAsync(organizations, github, cancellationToken).ConfigureAwait(false);

		// What we publish ourselves is decided here, by NuGet's own owner: search, and read back by
		// PKG-05/06/07: a release of ours gets no grace period against nuget.org. Recorded before the
		// rows are built, because a package belonging to no repository we govern is still ours.
		RecordOwnedPackages(_owned, packages);

		// Whether a repository is ours at all is decided before anything is read from disk on the
		// strength of it: NuGet's owner: search says who owns the package, never who owns the
		// repository behind it. Every organisation under management is consulted, not merely the one
		// being discovered: refreshing one must not decide that another's repositories belong to
		// somebody else.
		// A package repository that has not published yet never appears in nuget.org's results.
		// GitHub's list completes the tree with those, and only those: a repository is added when its
		// project files declare a PackageId, so applications, scripts and demos stay out.
		var published = PublishedRepositories(packages);

		var gitHubRepositories = await ListGitHubRepositoriesAsync(
			organizations,
			github is null ? null : (owner, ct) => ListOrganizationRepositoriesAsync(github, owner, published, _packageIdCache, _logger, ct),
			_logger,
			cancellationToken).ConfigureAwait(false);

		var (rows, ungoverned) = BuildRows(
			packages,
			_cache.GetCachedRows() ?? [],
			_runtimeSettings.Organizations,
			gitHubRepositories);

		_cache.SetUngovernedPackages(ungoverned);

		foreach (var row in rows)
		{
			await ApplyLocalStateAsync(row, cancellationToken).ConfigureAwait(false);
		}

		return rows;
	}

	private async Task<List<NuGetPackageInfo>> DiscoverOrganizationsAsync(
		IEnumerable<string> organizations,
		IGitHubClient? github,
		CancellationToken cancellationToken)
	{
		var packages = new List<NuGetPackageInfo>();

		using var canonicalLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
		var canonicalNames = github is null
			? null
			: new CanonicalRepositoryLookup(
				github,
				canonicalLoggerFactory.CreateLogger<CanonicalRepositoryLookup>());

		// Discover each organisation in turn and concatenate. Sequential rather than parallel: this
		// runs once at start-up, and NuGet's search API is the shared resource being paged through.
		foreach (var owner in organizations)
		{
			var discovered = await _nuget
				.DiscoverOrganizationPackagesAsync(owner, cancellationToken, canonicalNames)
				.ConfigureAwait(false);

			packages.AddRange(discovered);
		}

		return packages;
	}

	private static HashSet<string> PublishedRepositories(List<NuGetPackageInfo> packages)
		=> new(
			packages
				.Where(package => package.RepositoryOwner is not null && package.RepositoryName is not null)
				.Select(package => $"{package.RepositoryOwner}/{package.RepositoryName}"),
			StringComparer.OrdinalIgnoreCase);

	private async Task ApplyLocalStateAsync(RepositoryDashboardRow row, CancellationToken cancellationToken)
	{
		_runtimeSettings.SetAutoExcluded(row.RepositoryFullName, row.AutoExclusionReason);

		// Local paths are keyed on the full owner/name identity, not the bare repository name.
		var isCloned = _localRepo.IsClonedLocally(row.RepositoryFullName);

		row.IsClonedLocally = isCloned;
		row.LocalPath = _localRepo.GetLocalPath(row.RepositoryFullName);
		row.SlnxPath = isCloned ? _localRepo.FindSlnxFile(row.RepositoryFullName) : null;
		row.Status = isCloned ? PackageStatus.NotAssessed : PackageStatus.NotCloned;

		if (!isCloned)
		{
			return;
		}

		row.CurrentBranch = await _localRepo
			.GetCurrentBranchAsync(row.RepositoryFullName, cancellationToken)
			.ConfigureAwait(false);

		row.IsWorkingTreeClean = await _localRepo
			.IsWorkingTreeCleanAsync(row.RepositoryFullName, cancellationToken)
			.ConfigureAwait(false);

		row.HasUnpushedCommits = await _localRepo
			.HasUnpushedCommitsAsync(row.RepositoryFullName, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Records every discovered package as one the estate publishes itself.
	/// </summary>
	/// <param name="owned">The catalogue the freshness rules read.</param>
	/// <param name="packages">The packages discovered from NuGet.</param>
	/// <remarks>
	/// Static and free of the network so the one decision it encodes — that a package with no
	/// repository behind it is still ours — can be tested without a dashboard.
	/// </remarks>
	internal static void RecordOwnedPackages(
		NuGetOwnedPackageCatalog owned,
		IReadOnlyList<NuGetPackageInfo> packages)
		=> owned.Record(packages.Select(p => p.PackageId));

	/// <summary>
	/// Turns discovered packages into one row per repository, plus the packages that belong to no
	/// repository we govern.
	/// </summary>
	/// <remarks>
	/// Static and free of the network so the grouping — the part with the interesting edge cases — can
	/// be tested without one.
	/// </remarks>
	/// <param name="packages">The packages discovered from NuGet.</param>
	/// <param name="previousRows">The rows from the last successful discovery, for carry-forward.</param>
	/// <param name="organizations">The organisations under management.</param>
	/// <param name="gitHubRepositories">
	/// What GitHub lists for each organisation that was asked, keyed by organisation. An organisation
	/// present with a null list was asked and could not be read: the unpackaged rows it had before are
	/// carried forward rather than lost. Null, or an organisation absent, means GitHub was not consulted
	/// and today's package-only behaviour applies.
	/// </param>
	internal static (List<RepositoryDashboardRow> Rows, List<UngovernedPackage> Ungoverned) BuildRows(
		IReadOnlyList<NuGetPackageInfo> packages,
		IReadOnlyList<RepositoryDashboardRow> previousRows,
		IReadOnlyList<string> organizations,
		IReadOnlyDictionary<string, IReadOnlyList<GitHubRepositoryInfo>?>? gitHubRepositories)
	{
		// A package whose nuspec we could not read keeps the repository we knew it by. Without this a
		// request going astray removes a repository from governance, which is how eight of them came to
		// be reported as declaring no repository at all.
		var previousByPackageId = IndexPreviousPackages(previousRows);

		var rows = new Dictionary<string, RepositoryDashboardRow>(StringComparer.OrdinalIgnoreCase);
		var ungoverned = new List<UngovernedPackage>();

		foreach (var package in packages)
		{
			AddPackage(rows, ungoverned, package, previousByPackageId, organizations);
		}

		foreach (var row in rows.Values)
		{
			row.Packages.Sort((left, right) =>
				string.Compare(left.PackageId, right.PackageId, StringComparison.OrdinalIgnoreCase));
		}

		AddGitHubRepositories(rows, previousRows, organizations, gitHubRepositories);

		return (
			[.. rows.Values.OrderBy(row => row.RepositoryFullName, StringComparer.OrdinalIgnoreCase)],
			ungoverned);
	}

	private static Dictionary<string, string> IndexPreviousPackages(IReadOnlyList<RepositoryDashboardRow> previousRows)
		=> previousRows
			.SelectMany(row => row.Packages.Select(package => (package.PackageId, row.RepositoryFullName)))
			.GroupBy(pair => pair.PackageId, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(
				group => group.Key,
				group => group.First().RepositoryFullName,
				StringComparer.OrdinalIgnoreCase);

	private static void AddPackage(
		Dictionary<string, RepositoryDashboardRow> rows,
		List<UngovernedPackage> ungoverned,
		NuGetPackageInfo package,
		Dictionary<string, string> previousByPackageId,
		IReadOnlyList<string> organizations)
	{
		var identity = IdentifyRepository(package, previousByPackageId);

		var reason = identity is null
			? ReasonForNoRepository(package)
			: GovernanceScope.ReasonNotGoverned(identity, organizations);

		if (reason is not null)
		{
			ungoverned.Add(new UngovernedPackage
			{
				PackageId = package.PackageId,
				Organization = package.Organization,
				DeclaredRepository = identity,
				Reason = reason
			});

			return;
		}

		if (!rows.TryGetValue(identity!, out var row))
		{
			row = new RepositoryDashboardRow
			{
				RepositoryFullName = identity!,
				Organization = package.Organization,
				RepositoryUrl = package.RepositoryUrl ?? $"https://github.com/{identity}"
			};

			rows[identity!] = row;
		}

		row.Packages.Add(new PublishedPackage
		{
			PackageId = package.PackageId,
			LatestVersion = package.LatestVersion
		});
	}

	/// <summary>
	/// Completes the rows with the repositories GitHub lists whose project files declare a package id,
	/// merged by full name so a repository that publishes packages keeps them and appears once. A
	/// repository that declares no package id is not ours to govern and gets no row.
	/// </summary>
	private static void AddGitHubRepositories(
		Dictionary<string, RepositoryDashboardRow> rows,
		IReadOnlyList<RepositoryDashboardRow> previousRows,
		IReadOnlyList<string> organizations,
		IReadOnlyDictionary<string, IReadOnlyList<GitHubRepositoryInfo>?>? gitHubRepositories)
	{
		if (gitHubRepositories is null)
		{
			return;
		}

		// Only rows that came from a declared package id are carried forward: a row left in the cache
		// by the time every GitHub repository was shown declares none, and is dropped here.
		var previousDeclared = previousRows
			.Where(row => row.IsUnpackaged && row.DeclaredPackageIds.Count > 0)
			.ToDictionary(row => row.RepositoryFullName, StringComparer.OrdinalIgnoreCase);

		foreach (var (organization, listed) in gitHubRepositories)
		{
			if (listed is null)
			{
				CarryForwardUnanswered(rows, previousDeclared, organization, organizations);
				continue;
			}

			foreach (var repository in listed)
			{
				MergeListed(rows, previousDeclared, repository, organization, organizations);
			}
		}
	}

	private static void CarryForwardUnanswered(
		Dictionary<string, RepositoryDashboardRow> rows,
		Dictionary<string, RepositoryDashboardRow> previousDeclared,
		string organization,
		IReadOnlyList<string> organizations)
	{
		// Asked and unanswered: keep what we already showed for this organisation, so a flaky
		// GitHub call cannot make unpublished package repositories vanish from the tree.
		foreach (var previous in previousDeclared.Values.Where(row =>
			string.Equals(OwnerOf(row.RepositoryFullName), organization, StringComparison.OrdinalIgnoreCase)
			&& !rows.ContainsKey(row.RepositoryFullName)
			&& GovernanceScope.ReasonNotGoverned(row.RepositoryFullName, organizations) is null))
		{
			rows[previous.RepositoryFullName] = CarryForward(previous);
		}
	}

	private static void MergeListed(
		Dictionary<string, RepositoryDashboardRow> rows,
		Dictionary<string, RepositoryDashboardRow> previousDeclared,
		GitHubRepositoryInfo repository,
		string organization,
		IReadOnlyList<string> organizations)
	{
		if (GovernanceScope.ReasonNotGoverned(repository.FullName, organizations) is not null)
		{
			return;
		}

		if (rows.TryGetValue(repository.FullName, out var published))
		{
			published.IsArchived = repository.IsArchived;
			published.IsFork = repository.IsFork;
			return;
		}

		if (repository.DeclaredPackageIds is { Count: > 0 } declared)
		{
			rows[repository.FullName] = new RepositoryDashboardRow
			{
				RepositoryFullName = repository.FullName,
				Organization = organization,
				RepositoryUrl = repository.Url ?? $"https://github.com/{repository.FullName}",
				DeclaredPackageIds = [.. declared],
				IsArchived = repository.IsArchived,
				IsFork = repository.IsFork
			};
		}
		else if (repository.DeclaredPackageIds is null
			&& previousDeclared.TryGetValue(repository.FullName, out var previous))
		{
			// Its project files could not be read this time: keep what they said last time.
			var row = CarryForward(previous);
			row.IsArchived = repository.IsArchived;
			row.IsFork = repository.IsFork;
			rows[repository.FullName] = row;
		}
	}

	private static RepositoryDashboardRow CarryForward(RepositoryDashboardRow previous) => new()
	{
		RepositoryFullName = previous.RepositoryFullName,
		Organization = previous.Organization,
		RepositoryUrl = previous.RepositoryUrl,
		DeclaredPackageIds = [.. previous.DeclaredPackageIds],
		IsArchived = previous.IsArchived,
		IsFork = previous.IsFork
	};

	private static string? OwnerOf(string repositoryFullName)
	{
		var segments = repositoryFullName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		return segments.Length < 2 ? null : segments[^2];
	}

	/// <summary>
	/// Lists each organisation's repositories, one entry per organisation asked.
	/// </summary>
	/// <param name="organizations">The organisations being discovered.</param>
	/// <param name="list">Lists one organisation's repositories, or null when GitHub is unavailable.</param>
	/// <param name="logger">Where a failure is reported.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <remarks>
	/// A failure never propagates: the organisation maps to null so <see cref="BuildRows"/> falls back to
	/// the package-only behaviour and keeps what it already had. Cancellation does propagate.
	/// </remarks>
	internal static async Task<Dictionary<string, IReadOnlyList<GitHubRepositoryInfo>?>> ListGitHubRepositoriesAsync(
		IEnumerable<string> organizations,
		Func<string, CancellationToken, Task<IReadOnlyList<GitHubRepositoryInfo>>>? list,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		var result = new Dictionary<string, IReadOnlyList<GitHubRepositoryInfo>?>(StringComparer.OrdinalIgnoreCase);

		foreach (var organization in organizations)
		{
			if (list is null)
			{
				result[organization] = null;
				continue;
			}

			try
			{
				result[organization] = await list(organization, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				logger.LogWarning(ex, "Could not list GitHub repositories for {Organization}; showing package-derived repositories only.", organization);
				result[organization] = null;
			}
		}

		return result;
	}

	private static async Task<GitHubRepositoryInfo> DescribeRepositoryAsync(
		IGitHubClient github,
		Repository repository,
		IReadOnlySet<string> published,
		DeclaredPackageIdCache packageIdCache,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		IReadOnlyList<string>? declared = null;

		if (!repository.Archived
			&& !repository.Fork
			&& !published.Contains(repository.FullName))
		{
			declared = repository.PushedAt is { } pushedAt
				&& packageIdCache.TryGet(repository.FullName, pushedAt, out var remembered)
					? remembered
					: await ReadDeclaredPackageIdsAsync(github, repository, packageIdCache, logger, cancellationToken).ConfigureAwait(false);
		}

		return new GitHubRepositoryInfo(
			repository.FullName,
			repository.HtmlUrl,
			repository.Archived,
			repository.Fork,
			declared);
	}

	private static async Task<IReadOnlyList<string>?> ReadDeclaredPackageIdsAsync(
		IGitHubClient github,
		Repository repository,
		DeclaredPackageIdCache packageIdCache,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		try
		{
			var declared = await DeclaredPackageIds
				.ReadAsync(github, repository.Owner.Login, repository.Name, repository.DefaultBranch ?? "HEAD", cancellationToken)
				.ConfigureAwait(false);

			// A repository with no push time (nothing in it yet) is read each time: there is
			// nothing to tell a changed answer from an unchanged one.
			if (repository.PushedAt is { } readAt)
			{
				packageIdCache.Set(repository.FullName, readAt, declared);
			}

			return declared;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning(ex, "Could not read the declared package ids of {Repository}.", repository.FullName);
			return null;
		}
	}

	/// <summary>
	/// Lists one organisation's repositories, reading the declared package ids of each one that might
	/// be an unpublished package repository.
	/// </summary>
	/// <param name="github">The GitHub client.</param>
	/// <param name="organization">The organisation to list.</param>
	/// <param name="published">
	/// Repositories that already publish packages: they are in the tree regardless, so their project
	/// files are not read.
	/// </param>
	/// <param name="packageIdCache">
	/// What was found last time, reused for a repository nobody has pushed to since, so a discovery
	/// costs GitHub calls only for the repositories that changed.
	/// </param>
	/// <param name="logger">Where a repository whose project files could not be read is reported.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <remarks>
	/// Archived repositories and forks are not read either: they publish nothing, and nothing would be
	/// done to them if they did. A repository whose files cannot be read is listed with its ids unknown,
	/// so <see cref="BuildRows"/> keeps what it knew rather than dropping it, and nothing is remembered
	/// for it, so the next discovery tries again.
	/// </remarks>
	private static async Task<IReadOnlyList<GitHubRepositoryInfo>> ListOrganizationRepositoriesAsync(
		IGitHubClient github,
		string organization,
		IReadOnlySet<string> published,
		DeclaredPackageIdCache packageIdCache,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		var repositories = await github.Repository
			.GetAllForOrg(organization, new ApiOptions { PageSize = 100 })
			.WaitAsync(cancellationToken)
			.ConfigureAwait(false);

		var result = new GitHubRepositoryInfo[repositories.Count];

		await Parallel.ForEachAsync(
			Enumerable.Range(0, repositories.Count),
			new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
			async (index, ct) => result[index] = await DescribeRepositoryAsync(github, repositories[index], published, packageIdCache, logger, ct).ConfigureAwait(false)).ConfigureAwait(false);

		packageIdCache.Save();

		// Repositories that packages point at but that the list does not show under that name: renamed, and
		// in this organisation's habit archived by being renamed. Asked for by the old name, GitHub says what
		// it is now, so one that is archived is known to be, whatever it is called today.
		var moved = await UnlistedRepositoryResolver.ResolveAsync(
			organization,
			published,
			result,
			(fullName, ct) => LookUpRepositoryAsync(github, fullName, ct),
			logger,
			cancellationToken).ConfigureAwait(false);

		return [.. result, .. moved];
	}

	/// <summary>
	/// What GitHub says a repository is now, following a rename, or null when it does not exist.
	/// </summary>
	/// <param name="github">The GitHub client.</param>
	/// <param name="fullName">The repository as <c>owner/name</c>, which may be a name it has since left.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	private static async Task<ResolvedRepository?> LookUpRepositoryAsync(
		IGitHubClient github,
		string fullName,
		CancellationToken cancellationToken)
	{
		var slash = fullName.IndexOf('/', StringComparison.Ordinal);

		try
		{
			var repository = await github.Repository
				.Get(fullName[..slash], fullName[(slash + 1)..])
				.WaitAsync(cancellationToken)
				.ConfigureAwait(false);

			return new ResolvedRepository(repository.FullName, repository.Archived, repository.Fork);
		}
		catch (NotFoundException)
		{
			return null;
		}
	}

	/// <summary>
	/// The repository a package belongs to, or null when it belongs to none we can name.
	/// </summary>
	private static string? IdentifyRepository(
		NuGetPackageInfo package,
		Dictionary<string, string> previousByPackageId)
	{
		if (package.RepositoryName is not null)
		{
			return $"{package.RepositoryOwner ?? package.Organization}/{package.RepositoryName}";
		}

		return package.ResolutionOutcome is RepositoryResolutionOutcome.LookupFailed
			&& previousByPackageId.TryGetValue(package.PackageId, out var previous)
				? previous
				: null;
	}

	/// <summary>
	/// Why a package with no repository has none — distinguishing a nuspec that declares nothing from
	/// one we never managed to read.
	/// </summary>
	private static string ReasonForNoRepository(NuGetPackageInfo package)
		=> package.ResolutionOutcome is RepositoryResolutionOutcome.LookupFailed
			? $"{UngovernedPackage.LookupFailedReasonPrefix} (network) — rediscover to try again."
			: "The package declares no repository in its nuspec.";

	/// <summary>
	/// Re-derives each row's local clone facts from what is actually on disk, leaving its assessment
	/// alone. Returns the number of rows whose cloned state changed.
	/// </summary>
	/// <remarks>
	/// The cache stores whether a repository was cloned, but nothing kept that in step with the disk: a
	/// folder deleted (or a clone root reconfigured) since the last refresh left rows claiming a checkout
	/// that is not there, and the toolbar offering to build and push it. The write guards fail closed on
	/// such a row, so the risk was a misleading display rather than a bad write — but "cloned" is exactly
	/// the count the estate view leads on, so it should be true.
	/// </remarks>
	public async Task<int> ReconcileLocalStateAsync(
		IEnumerable<RepositoryDashboardRow> rows,
		CancellationToken cancellationToken)
	{
		var changed = 0;

		var governed = _runtimeSettings.Organizations;

		foreach (var row in rows)
		{
			// Rules change, and a cached row can predate the ones in force. Re-judging here means a
			// repository that stopped being ours cannot keep its clone facts — and so its buttons —
			// merely by having been governed when the cache was written.
			GovernanceScope.Apply(row, governed);

			// Cached rows carry what GitHub said about them, so archived and fork repositories are
			// excluded from the first render rather than only after the next discovery.
			_runtimeSettings.SetAutoExcluded(row.RepositoryFullName, row.AutoExclusionReason);

			var repoIdentity = RepoIdentity(row);
			if (repoIdentity is null || !row.IsGoverned)
			{
				continue;
			}

			row.LocalPath = _localRepo.GetLocalPath(repoIdentity);

			var isCloned = _localRepo.IsClonedLocally(repoIdentity);
			if (isCloned == row.IsClonedLocally)
			{
				continue;
			}

			row.IsClonedLocally = isCloned;
			row.SlnxPath = isCloned ? _localRepo.FindSlnxFile(repoIdentity) : null;
			changed++;

			if (isCloned)
			{
				// Read the facts the toolbar gates on, or a newly-found clone would sit there with an
				// unknown branch and every local action refusing to run until something else filled it in.
				row.CurrentBranch = await _localRepo.GetCurrentBranchAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
				row.IsWorkingTreeClean = await _localRepo.IsWorkingTreeCleanAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
				row.HasUnpushedCommits = await _localRepo.HasUnpushedCommitsAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				// Everything below was read out of a checkout that is no longer there.
				row.CurrentBranch = null;
				row.IsWorkingTreeClean = null;
				row.HasUnpushedCommits = null;
				row.IsSyncedWithOrigin = null;
				row.SyncStatusCheckedAtUtc = null;
				row.LatestTag = null;
			}
		}

		return changed;
	}
}
