using System.Globalization;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// One repository's sighting of an uncovered dependency: a valid Dependabot pull request that nothing
/// here can fix automatically.
/// </summary>
/// <param name="RepositoryFullName">Where it was seen, as "owner/name".</param>
/// <param name="PullRequestNumber">The pull request number.</param>
/// <param name="FromVersion">The version the pull request believes is declared.</param>
/// <param name="ToVersion">The version it would move to.</param>
/// <param name="HtmlUrl">The pull request's web address, which identifies the sighting.</param>
public sealed record UncoveredDependencySighting(
	string RepositoryFullName,
	int PullRequestNumber,
	string FromVersion,
	string ToVersion,
	string HtmlUrl);

/// <summary>
/// Raises and maintains one issue per uncovered dependency against this application's own repository.
/// </summary>
/// <remarks>
/// One issue per <em>dependency</em>, not per sighting: the deliverable is a remediation somebody has
/// to write, and the same gap across eight repositories is still one piece of work. Evidence
/// accumulates in the issue body, keyed on each pull request's URL, so a re-run of triage adds what is
/// new and writes nothing when there is nothing new.
/// <para>
/// Triage runs on many repository lanes concurrently, so two repositories can hit the same gap at the
/// same moment. Creation is serialised per marker and the lookup repeated inside the lock, otherwise
/// the first estate-wide sweep would open one issue per repository for a single gap.
/// </para>
/// </remarks>
/// <param name="targetRepositoryFullName">
/// The repository the issues are raised against — this application's own, by default.
/// </param>
/// <remarks>
/// A singleton, and the GitHub ports arrive per call rather than in the constructor. They cannot be
/// injected: a client is built from the signed-in user's token when a work item runs, so there is no
/// one client to hold. What has to be shared across lanes is the state below, and that is exactly
/// what a singleton holding no client can share.
/// </remarks>
public sealed class UncoveredDependencyIssueService(string targetRepositoryFullName)
{
	private const string _evidenceHeader = "| Repository | Pull request | Proposed |";
	private const string _evidenceDivider = "| --- | --- | --- |";

	private readonly SemaphoreSlim _gate = new(1, 1);

	/// <summary>
	/// The issues this process has raised or added to, by marker.
	/// </summary>
	/// <remarks>
	/// Not an optimisation. The issue list GitHub returns does not reflect a create immediately, so a
	/// lane looking the gap up moments after another lane raised it is told the gap is unraised and
	/// raises it again. Serialising the section is not enough on its own — the lookup inside the lock
	/// can still be stale — so what this process wrote is remembered instead of re-read.
	/// </remarks>
	private readonly Dictionary<string, (int Number, string Body)> _raised =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// The title of the issue that tracks a dependency's missing remediation.
	/// </summary>
	/// <param name="dependency">The uncovered dependency.</param>
	public static string TitleFor(DependencyRef dependency)
		=> $"No auto-remediation for {Slug(dependency.Ecosystem)}: {dependency.Name}";

	/// <summary>
	/// The hidden marker that identifies a dependency's issue on a later run.
	/// </summary>
	/// <param name="dependency">The uncovered dependency.</param>
	/// <remarks>
	/// Lower-cased, because a dependency named two ways is still one gap and must not become two
	/// issues.
	/// </remarks>
	public static string MarkerFor(DependencyRef dependency)
		=> $"<!-- nugetmgmt:uncovered:{Slug(dependency.Ecosystem)}/"
			+ $"{dependency.Name.ToLowerInvariant()} -->";

	/// <summary>
	/// Records that a dependency is uncovered, creating the issue or adding to it as needed.
	/// </summary>
	/// <param name="readApi">For finding an issue this gap already has.</param>
	/// <param name="writeApi">For raising or updating it.</param>
	/// <param name="dependency">The uncovered dependency.</param>
	/// <param name="sightings">Where it was seen this time.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	public async Task ReportAsync(
		IGitHubIssueApi readApi,
		IGitHubWriteApi writeApi,
		DependencyRef dependency,
		IReadOnlyList<UncoveredDependencySighting> sightings,
		CancellationToken cancellationToken)
	{
		if (sightings.Count == 0)
		{
			return;
		}

		var (owner, name) = Split(targetRepositoryFullName);
		var marker = MarkerFor(dependency);

		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			// What this process already wrote is trusted ahead of the lookup, which can be stale.
			if (!_raised.TryGetValue(marker, out var known))
			{
				var found = await FindAsync(readApi, owner, name, marker, cancellationToken)
					.ConfigureAwait(false);

				if (found is null)
				{
					var body = NewBody(dependency, marker, sightings);

					var number = await writeApi
						.CreateIssueAsync(owner, name, TitleFor(dependency), body, [], cancellationToken)
						.ConfigureAwait(false);

					_raised[marker] = (number, body);

					return;
				}

				known = (found.Number, found.Body ?? string.Empty);
			}

			var unseen = sightings
				.Where(s => !known.Body.Contains(s.HtmlUrl, StringComparison.OrdinalIgnoreCase))
				.ToList();

			if (unseen.Count == 0)
			{
				return;
			}

			var updated = Append(known.Body, unseen);

			await writeApi
				.UpdateIssueBodyAsync(owner, name, known.Number, updated, cancellationToken)
				.ConfigureAwait(false);

			_raised[marker] = (known.Number, updated);
		}
		finally
		{
			_gate.Release();
		}
	}

	private static async Task<GitHubOpenItem?> FindAsync(
		IGitHubIssueApi readApi,
		string owner,
		string name,
		string marker,
		CancellationToken cancellationToken)
	{
		var openItems = await readApi
			.GetOpenItemsAsync(owner, name, cancellationToken)
			.ConfigureAwait(false);

		return openItems.FirstOrDefault(item =>
			!item.IsPullRequest
			&& item.Body?.Contains(marker, StringComparison.OrdinalIgnoreCase) == true);
	}

	/// <summary>
	/// The issue body for a gap seen for the first time. The evidence table is last so that later
	/// sightings are a plain append.
	/// </summary>
	private static string NewBody(
		DependencyRef dependency,
		string marker,
		IReadOnlyList<UncoveredDependencySighting> sightings)
		=> string.Join(
			"\n",
			marker,
			string.Empty,
			$"Dependabot is raising version bumps for `{dependency.Name}` "
				+ $"({Slug(dependency.Ecosystem)}) that this application judges valid but cannot fix: no "
				+ "rule enforces a minimum version of it, so no remediation moves it.",
			string.Empty,
			"Either add a rule that governs this dependency and a remediation for it, or decide the gap "
				+ "is deliberate and close this issue. Until one or the other happens, every pull request "
				+ "below has to be handled by hand.",
			string.Empty,
			"Seen in:",
			string.Empty,
			_evidenceHeader,
			_evidenceDivider,
			string.Join("\n", sightings.Select(Row)))
			+ "\n";

	private static string Append(string body, IReadOnlyList<UncoveredDependencySighting> sightings)
		=> body.TrimEnd('\n') + "\n" + string.Join("\n", sightings.Select(Row)) + "\n";

	private static string Row(UncoveredDependencySighting sighting)
		=> $"| {sighting.RepositoryFullName} | [#{sighting.PullRequestNumber.ToString(CultureInfo.InvariantCulture)}]"
			+ $"({sighting.HtmlUrl}) | {sighting.FromVersion} → {sighting.ToVersion} |";

	private static string Slug(DependencyEcosystem ecosystem) => ecosystem switch
	{
		DependencyEcosystem.GitHubActions => "github-actions",
		DependencyEcosystem.NuGet => "nuget",
		_ => "unknown"
	};

	private static (string Owner, string Name) Split(string fullName)
	{
		var parts = fullName.Split('/', 2);

		return parts.Length == 2
			? (parts[0], parts[1])
			: throw new ArgumentException(
				$"'{fullName}' is not an owner/name repository.", nameof(fullName));
	}
}
