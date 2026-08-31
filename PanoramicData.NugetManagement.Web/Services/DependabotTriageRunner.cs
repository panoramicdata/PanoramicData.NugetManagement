using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// What one repository's triage pass did.
/// </summary>
/// <param name="Closed">Pull requests closed as already satisfied.</param>
/// <param name="Covered">Still-valid pull requests a remediation will handle.</param>
/// <param name="Uncovered">Still-valid pull requests that exposed a gap, and so raised issues.</param>
/// <param name="Idle">
/// Still-valid pull requests whose dependency is governed by a rule that is not failing for it now.
/// Nothing is queued to move them, and nothing is wrong with the rule set either, so they are
/// reported and left alone.
/// </param>
/// <param name="Unrecognised">Pull requests left alone.</param>
public sealed record DependabotTriageOutcome(
	int Closed,
	int Covered,
	int Uncovered,
	int Idle,
	int Unrecognised);

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
		var idle = 0;
		var unrecognised = 0;

		var uncovered = new Dictionary<DependencyRef, List<UncoveredDependencySighting>>();

		// Dependencies this pass found a fix for, or found already done. Any gap issue standing against
		// one of them is answering a question that is no longer open, so it is retracted below.
		var resolved = new Dictionary<DependencyRef, string>();

		foreach (var triage in triages)
		{
			cancellationToken.ThrowIfCancellationRequested();

			switch (triage.Verdict)
			{
				case DependabotVerdict.AlreadySatisfied:
					await CloseAsync(writeApi, owner, name, triage, onOutput, cancellationToken)
						.ConfigureAwait(false);
					closed++;

					if (triage.Proposal is { } satisfied)
					{
						resolved[satisfied.Dependency] =
							$"{repositoryFullName} now declares it at or above the proposed version";
					}

					break;

				case DependabotVerdict.ValidCovered:
					covered++;
					onOutput($"↺ #{triage.Issue.Number} left open: {triage.Reason}");

					if (triage.Proposal is { } covering && triage.CoveringRuleId is { } coveringRuleId)
					{
						resolved[covering.Dependency] =
							$"{coveringRuleId} governs it and its remediation will move it";
					}

					break;

				// Governed, but no failure of that rule is queued to move it today. Said out loud and
				// otherwise left alone: it is not a gap, and an issue for it would be noise.
				case DependabotVerdict.ValidUncovered when !triage.IsRuleSetGap:
					idle++;
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
				$"🐛 Nothing here can ever move {dependency.Name} — raising or updating the gap issue "
				+ $"for it ({sightings.Count} pull request(s)).");

			await uncoveredIssues
				.ReportAsync(readApi, writeApi, dependency, sightings, cancellationToken)
				.ConfigureAwait(false);
		}

		foreach (var (dependency, reason) in resolved)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var retracted = await uncoveredIssues
				.RetractAsync(readApi, writeApi, dependency, reason, cancellationToken)
				.ConfigureAwait(false);

			if (retracted is { } number)
			{
				onOutput($"✅ Closing gap issue #{number} for {dependency.Name}: {reason}.");
			}
		}

		return new DependabotTriageOutcome(
			closed,
			covered,
			uncovered.Sum(entry => entry.Value.Count),
			idle,
			unrecognised);
	}

	/// <summary>
	/// The open items as they stand after a triage pass: each judged item carrying its verdict, and the
	/// closed ones gone.
	/// </summary>
	/// <param name="issues">The repository's open items as they were before the pass.</param>
	/// <param name="triages">The verdicts reached.</param>
	/// <remarks>
	/// The closed ones are dropped here rather than waiting for the next refresh to notice, because the
	/// tree would otherwise go on showing pull requests this application has just closed — and the
	/// staleness clock on them would keep running.
	/// </remarks>
	public static IReadOnlyList<RepositoryIssue> Restamp(
		IReadOnlyList<RepositoryIssue> issues,
		IReadOnlyList<DependabotTriage> triages)
	{
		var byNumber = triages.ToDictionary(t => t.Issue.Number);
		var remaining = new List<RepositoryIssue>();

		foreach (var issue in issues)
		{
			if (!byNumber.TryGetValue(issue.Number, out var triage))
			{
				remaining.Add(issue);
				continue;
			}

			if (triage.Verdict == DependabotVerdict.AlreadySatisfied)
			{
				continue;
			}

			issue.TriageVerdict = triage.Verdict;
			issue.TriageReason = triage.Reason;
			remaining.Add(issue);
		}

		return remaining;
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
