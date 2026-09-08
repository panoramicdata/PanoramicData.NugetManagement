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
/// <param name="Adopted">
/// Pull requests whose bumps were written to the local clone and which were then closed. Counts what
/// was actually written, not what was found adoptable: a plan that matched nothing closes nothing and
/// is reported as idle instead.
/// </param>
/// <param name="Obsolete">
/// Pull requests closed because the repository no longer references what they propose to move.
/// Counted apart from <paramref name="Closed"/> because the two claims are different: one says the
/// repository has already gone further, the other says it has dropped the dependency entirely.
/// </param>
public sealed record DependabotTriageOutcome(
	int Closed,
	int Covered,
	int Uncovered,
	int Idle,
	int Unrecognised,
	int Adopted = 0,
	int Obsolete = 0);

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
	/// The hidden marker on a comment closing a pull request whose bumps this application has adopted
	/// into the local clone.
	/// </summary>
	/// <remarks>
	/// Separate from <see cref="ClosedMarker"/> because the two are different claims: one says the
	/// repository had already outgrown the pull request, the other says this application has just
	/// written what the pull request proposed. Somebody reading the history needs to tell them apart —
	/// and the second is the one that leaves an uncommitted change behind.
	/// </remarks>
	public const string AdoptedMarker = "<!-- nugetmgmt:closed:adopted -->";

	/// <summary>
	/// The hidden marker on a comment closing a pull request for a dependency the repository no longer
	/// references anywhere it could be declaring one.
	/// </summary>
	/// <remarks>
	/// The third and last reason a close happens, and the only one made on the strength of not finding
	/// something. Kept distinct from the other two so that a close made in error — the one failure mode
	/// this verdict has — can be found by searching for its marker rather than read out of prose.
	/// </remarks>
	public const string ObsoleteMarker = "<!-- nugetmgmt:closed:no-longer-referenced -->";

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
	/// <param name="adopter">
	/// Writes an adoption plan to the local clone, or null when there is no clone to write to — in
	/// which case nothing is adopted and no pull request is closed on that basis.
	/// </param>
	public async Task<DependabotTriageOutcome> RunAsync(
		IGitHubIssueApi readApi,
		IGitHubWriteApi writeApi,
		string repositoryFullName,
		IReadOnlyList<DependabotTriage> triages,
		Action<string> onOutput,
		CancellationToken cancellationToken,
		IBumpAdopter? adopter = null)
	{
		var (owner, name) = Split(repositoryFullName);
		var closed = 0;
		var covered = 0;
		var idle = 0;
		var unrecognised = 0;
		var adopted = 0;
		var obsolete = 0;

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
					await CloseAsync(
							writeApi, owner, name, triage, ClosedMarker, onOutput, cancellationToken)
						.ConfigureAwait(false);
					closed++;

					if (triage.Proposal is { } satisfied)
					{
						foreach (var bump in satisfied.Bumps)
						{
							resolved[bump.Dependency] =
								$"{repositoryFullName} now declares it at or above the proposed version";
						}
					}

					break;

				// The repository has dropped the dependency, so there is nothing here for the pull request
				// to move and nothing missing that somebody should write.
				case DependabotVerdict.Obsolete:
					await CloseAsync(
							writeApi, owner, name, triage, ObsoleteMarker, onOutput, cancellationToken)
						.ConfigureAwait(false);
					obsolete++;

					if (triage.Proposal is { } dropped)
					{
						foreach (var bump in dropped.Bumps)
						{
							resolved[bump.Dependency] =
								$"{repositoryFullName} no longer references it anywhere it could be declared";
						}
					}

					break;

				case DependabotVerdict.ValidCovered:
					covered++;
					onOutput($"↺ #{triage.Issue.Number} left open: {triage.Reason}");

					if (triage.Proposal is { } covering && triage.CoveringRuleId is { } coveringRuleId)
					{
						foreach (var bump in covering.Bumps)
						{
							resolved[bump.Dependency] =
								$"{coveringRuleId} governs it and its remediation will move it";
						}
					}

					break;

				// Old enough that no grace period is still protecting anything, and every outstanding
				// bump can be written. Write first, and close only if the write actually landed.
				case DependabotVerdict.Adoptable when triage.Adoption is { } plan:
					if (adopter is null)
					{
						idle++;
						onOutput(
							$"↺ #{triage.Issue.Number} left open: adoptable, but there is no local clone to "
							+ "write the bump to.");
						break;
					}

					onOutput($"🔧 #{triage.Issue.Number}: {triage.Reason}");

					var written = adopter.Adopt(plan, onOutput);

					// A writer that matched nothing is indistinguishable from one that succeeded, so the
					// close has to be conditional on there being something to show for it. This is also the
					// ordinary case when two pull requests in one pass propose the same package.
					if (written.Count == 0)
					{
						idle++;
						onOutput(
							$"↺ #{triage.Issue.Number} left open: adoption wrote nothing, so closing it "
							+ "would close it against no change at all.");
						break;
					}

					onOutput($"✏️ Wrote {string.Join(", ", written)} in the local clone.");

					await CloseAsync(
							writeApi, owner, name, triage, AdoptedMarker, onOutput, cancellationToken)
						.ConfigureAwait(false);
					adopted++;

					if (triage.Proposal is { } adoptedProposal)
					{
						foreach (var bump in adoptedProposal.Bumps)
						{
							resolved[bump.Dependency] =
								$"{repositoryFullName} has adopted the proposed version locally";
						}
					}

					break;

				// Governed, but no failure of that rule is queued to move it today. Said out loud and
				// otherwise left alone: it is not a gap, and an issue for it would be noise.
				case DependabotVerdict.ValidUncovered when !triage.IsRuleSetGap:
					idle++;
					onOutput($"↺ #{triage.Issue.Number} left open: {triage.Reason}");
					break;

				// One sighting per bump that is nobody's job, not per pull request. A grouped pull request
				// that is part covered and part gap raises an issue only for the gap half.
				case DependabotVerdict.ValidUncovered when triage.Proposal is { } proposal:
					foreach (var bump in triage.GapBumps)
					{
						if (!uncovered.TryGetValue(bump.Dependency, out var sightings))
						{
							sightings = [];
							uncovered[bump.Dependency] = sightings;
						}

						sightings.Add(new UncoveredDependencySighting(
							repositoryFullName,
							proposal.Number,
							bump.FromVersion,
							bump.ToVersion,
							proposal.HtmlUrl));
					}

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
			unrecognised,
			adopted,
			obsolete);
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

			// Both of these close unconditionally, so neither is open any more.
			if (triage.Verdict is DependabotVerdict.AlreadySatisfied or DependabotVerdict.Obsolete)
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
	/// <param name="writeApi">For commenting and closing.</param>
	/// <param name="owner">The repository owner.</param>
	/// <param name="name">The repository name.</param>
	/// <param name="triage">The verdict being acted on.</param>
	/// <param name="marker">
	/// The hidden marker identifying why this close happened: <see cref="ClosedMarker"/> for a pull
	/// request the repository had already outgrown, <see cref="AdoptedMarker"/> for one whose bumps
	/// were just written.
	/// </param>
	/// <param name="onOutput">Where the intended close is announced before it is made.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	private async Task CloseAsync(
		IGitHubWriteApi writeApi,
		string owner,
		string name,
		DependabotTriage triage,
		string marker,
		Action<string> onOutput,
		CancellationToken cancellationToken)
	{
		var key = $"{owner}/{name}#{triage.Issue.Number}";

		onOutput($"✂️ Closing #{triage.Issue.Number} ({triage.Issue.Title}): {triage.Reason}");

		if (_commented.Add(key))
		{
			await writeApi
				.CommentAsync(
					owner, name, triage.Issue.Number, CommentBody(triage, marker), cancellationToken)
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
	private static string CommentBody(DependabotTriage triage, string marker)
		=> string.Join(
			"\n",
			marker,
			string.Empty,
			$"Closing automatically: {triage.Reason}",
			string.Empty,
			Provenance(marker));

	/// <summary>
	/// The paragraph explaining who closed this and on what grounds. One per marker: the grounds
	/// differ, and a reader deciding whether to reopen needs to know which claim is being made.
	/// </summary>
	private static string Provenance(string marker) => marker switch
	{
		AdoptedMarker =>
			"Raised by PanoramicData.NugetManagement's Dependabot triage, which has written these "
				+ "versions into the repository directly rather than merging this pull request. If this "
				+ "is wrong, reopen it — and the mistake is worth reporting.",

		ObsoleteMarker =>
			"Raised by PanoramicData.NugetManagement's Dependabot triage, which found no reference to "
				+ "this dependency in any project file, props or targets file, packages.config, tool "
				+ "manifest or workflow in the repository. If it is declared somewhere that list misses, "
				+ "reopen it — and please report it, because the missing place is a bug here.",

		_ =>
			"Raised by PanoramicData.NugetManagement's Dependabot triage. If this is wrong, reopen it — "
				+ "and the mistake is worth reporting, because triage only closes pull requests whose "
				+ "target version the repository already declares."
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
