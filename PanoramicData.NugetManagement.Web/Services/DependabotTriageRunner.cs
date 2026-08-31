using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// What one repository's triage pass did.
/// </summary>
/// <param name="Closed">Pull requests closed as already satisfied.</param>
/// <param name="Covered">Still-valid pull requests a remediation will handle.</param>
/// <param name="Uncovered">Still-valid pull requests nothing can handle, which raised issues.</param>
/// <param name="Unrecognised">Pull requests left alone.</param>
public sealed record DependabotTriageOutcome(int Closed, int Covered, int Uncovered, int Unrecognised);

/// <summary>
/// Carries out what triage decided: closes the redundant pull requests and raises an issue for each
/// dependency nothing here can fix.
/// </summary>
/// <remarks>
/// Separate from the work executor so the writes can be tested without a dashboard, a clone or a
/// network. The executor's job is to gather the inputs; this decides nothing, and the verdicts it
/// acts on come from <see cref="DependabotTriageService"/>.
/// </remarks>
/// <param name="uncoveredIssues">Raises the issue for a dependency nothing governs.</param>
/// <remarks>
/// A singleton, and the GitHub ports arrive per call: a client is built from the signed-in token when
/// a work item runs, so there is none to inject. Being a singleton is what lets
/// <see cref="_commented"/> mean "this process", across every lane.
/// </remarks>
public sealed class DependabotTriageRunner(UncoveredDependencyIssueService uncoveredIssues)
{
	/// <summary>
	/// The hidden marker on a closing comment, identifying it as this application's.
	/// </summary>
	public const string ClosedMarker = "<!-- nugetmgmt:closed:already-satisfied -->";

	/// <summary>
	/// The pull requests this process has already commented on, as "owner/name#number".
	/// </summary>
	/// <remarks>
	/// Closing removes a pull request from the open list, so a later pass should not see it again at
	/// all. This covers the case where the close failed after the comment landed: the explanation is
	/// already there, and repeating it would only add noise to somebody's pull request.
	/// </remarks>
	private readonly HashSet<string> _commented = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Acts on one repository's verdicts.
	/// </summary>
	/// <param name="readApi">For finding an already-raised gap issue.</param>
	/// <param name="writeApi">For commenting, closing and raising.</param>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <param name="triages">The verdicts, as triage reached them.</param>
	/// <param name="onOutput">
	/// Where each intended action is announced. Every GitHub mutation is announced before it is made,
	/// so the work item's output is the audit trail for it.
	/// </param>
	/// <param name="cancellationToken">A cancellation token.</param>
	public async Task<DependabotTriageOutcome> RunAsync(
		IGitHubIssueApi readApi,
		IGitHubWriteApi writeApi,
		string repositoryFullName,
		IReadOnlyList<DependabotTriage> triages,
		Action<string> onOutput,
		CancellationToken cancellationToken)
	{
		var (owner, name) = Split(repositoryFullName);
		var closed = 0;
		var covered = 0;
		var unrecognised = 0;

		var uncovered = new Dictionary<DependencyRef, List<UncoveredDependencySighting>>();

		foreach (var triage in triages)
		{
			cancellationToken.ThrowIfCancellationRequested();

			switch (triage.Verdict)
			{
				case DependabotVerdict.AlreadySatisfied:
					await CloseAsync(writeApi, owner, name, triage, onOutput, cancellationToken)
						.ConfigureAwait(false);
					closed++;
					break;

				case DependabotVerdict.ValidCovered:
					covered++;
					onOutput($"↺ #{triage.Issue.Number} left open: {triage.Reason}");
					break;

				case DependabotVerdict.ValidUncovered when triage.Proposal is { } proposal:
					if (!uncovered.TryGetValue(proposal.Dependency, out var sightings))
					{
						sightings = [];
						uncovered[proposal.Dependency] = sightings;
					}

					sightings.Add(new UncoveredDependencySighting(
						repositoryFullName,
						proposal.Number,
						proposal.FromVersion,
						proposal.ToVersion,
						proposal.HtmlUrl));

					break;

				default:
					unrecognised++;
					break;
			}
		}

		foreach (var (dependency, sightings) in uncovered)
		{
			cancellationToken.ThrowIfCancellationRequested();

			onOutput(
				$"🐛 No remediation governs {dependency.Name} — raising or updating the gap issue for it "
				+ $"({sightings.Count} pull request(s)).");

			await uncoveredIssues
				.ReportAsync(readApi, writeApi, dependency, sightings, cancellationToken)
				.ConfigureAwait(false);
		}

		return new DependabotTriageOutcome(
			closed,
			covered,
			uncovered.Sum(entry => entry.Value.Count),
			unrecognised);
	}

	/// <summary>
	/// Explains, then closes. In that order, so a human who finds the pull request closed has
	/// something to read — and so a failure to close still leaves the explanation behind.
	/// </summary>
	private async Task CloseAsync(
		IGitHubWriteApi writeApi,
		string owner,
		string name,
		DependabotTriage triage,
		Action<string> onOutput,
		CancellationToken cancellationToken)
	{
		var key = $"{owner}/{name}#{triage.Issue.Number}";

		onOutput($"✂️ Closing #{triage.Issue.Number} ({triage.Issue.Title}): {triage.Reason}");

		if (_commented.Add(key))
		{
			await writeApi
				.CommentAsync(owner, name, triage.Issue.Number, CommentBody(triage), cancellationToken)
				.ConfigureAwait(false);
		}

		await writeApi
			.ClosePullRequestAsync(owner, name, triage.Issue.Number, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// The closing comment. Says what happened and why, in terms a human reading the pull request can
	/// act on, and carries the marker so its provenance is obvious.
	/// </summary>
	private static string CommentBody(DependabotTriage triage)
		=> string.Join(
			"\n",
			ClosedMarker,
			string.Empty,
			$"Closing automatically: {triage.Reason}",
			string.Empty,
			"Raised by PanoramicData.NugetManagement's Dependabot triage. If this is wrong, reopen it — "
				+ "and the mistake is worth reporting, because triage only closes pull requests whose "
				+ "target version the repository already declares.");

	private static (string Owner, string Name) Split(string fullName)
	{
		var parts = fullName.Split('/', 2);

		return parts.Length == 2
			? (parts[0], parts[1])
			: throw new ArgumentException(
				$"'{fullName}' is not an owner/name repository.", nameof(fullName));
	}
}
