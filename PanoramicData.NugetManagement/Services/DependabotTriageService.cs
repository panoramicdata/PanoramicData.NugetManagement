using NuGet.Versioning;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// One open Dependabot pull request, and what triage concluded about it.
/// </summary>
/// <param name="Issue">The pull request, as the issue list reported it.</param>
/// <param name="Proposal">What it proposes, or null when the title was not recognised.</param>
/// <param name="Verdict">The conclusion.</param>
/// <param name="Reason">
/// Why, in a sentence. Written into the closing comment and the work item log, so it has to read as
/// an explanation to a human rather than a status code.
/// </param>
/// <param name="CoveringRuleId">
/// The failing rule whose remediation covers this, when the verdict is
/// <see cref="DependabotVerdict.ValidCovered"/>; otherwise null.
/// </param>
/// <param name="IsRuleSetGap">
/// Whether this is a gap in the rule set rather than a rule that simply has nothing to say today.
/// </param>
/// <param name="GapBumpsOrNull">
/// Backing for <see cref="GapBumps"/>. Positional so the record stays positional; read through the
/// property, which normalizes null to empty.
/// </param>
/// <param name="Adoption">
/// What adopting this pull request would write, or null when the verdict is not
/// <see cref="DependabotVerdict.Adoptable"/>.
/// </param>
/// <remarks>
/// <see cref="IsRuleSetGap"/> is what decides whether an issue is raised. Both it and
/// <see cref="DependabotVerdict.ValidUncovered"/> mean "no fix is coming for this right now", but only
/// one of them is somebody's work: a dependency no rule governs, or one governed by rules that cannot
/// see where it is declared, will never be fixed until a human writes something. A governed dependency
/// whose rule is merely passing needs no issue — the rule will fail when it should, and raising an
/// issue for the interval in between is how one triage pass produced twenty issues nobody asked for.
/// </remarks>
public sealed record DependabotTriage(
	RepositoryIssue Issue,
	DependabotProposal? Proposal,
	DependabotVerdict Verdict,
	string Reason,
	string? CoveringRuleId,
	bool IsRuleSetGap = false,
	IReadOnlyList<DependabotBump>? GapBumpsOrNull = null,
	DependabotAdoptionPlan? Adoption = null)
{
	/// <summary>
	/// The bumps in this pull request that nothing governs, or that are governed by a rule which never
	/// reads where they are declared. Empty unless <see cref="IsRuleSetGap"/>.
	/// </summary>
	/// <remarks>
	/// A grouped pull request can be part covered and part gap, and only the gap half is somebody's
	/// work. Raising an issue for the covered half would be raising one against a fix that is already
	/// queued.
	/// </remarks>
	public IReadOnlyList<DependabotBump> GapBumps => GapBumpsOrNull ?? [];
}

/// <summary>
/// Decides what to do about each of a repository's open Dependabot pull requests.
/// </summary>
/// <remarks>
/// Pure apart from a clock: no I/O and no GitHub. Everything else it needs is the pull requests, what
/// the repository declares, which rules are failing, and whether a rule has a remediation. That last
/// one arrives as a predicate rather than a dependency on <c>RemediationRegistry</c>, which lives in
/// the web project — keeping the existing rules-in-core, remediations-in-web seam intact.
/// <para>
/// The clock is needed only for the adoption age gate, and arrives as a <see cref="TimeProvider"/> so
/// a test can stand either side of the threshold.
/// </para>
/// </remarks>
public sealed class DependabotTriageService
{
	/// <summary>
	/// How long a still-valid pull request may stay open before its bumps are adopted regardless of
	/// whether any rule is failing for them.
	/// </summary>
	/// <remarks>
	/// Sixty days sits above PKG-05's 30-day build grace and below PKG-06's 90-day minor grace. It is
	/// not trying to mirror the graces — it is a backstop against a pull request rotting, and its only
	/// job is to be long enough that nothing is adopted while a grace period is still doing useful
	/// work.
	/// <para>
	/// A constant rather than a setting. One number nobody has asked to change is not worth a settings
	/// row — and a new <c>RuntimeSettings</c> property has to be added to the hand-written
	/// <c>SaveToDisk</c> snapshot or every save silently erases it.
	/// </para>
	/// </remarks>
	public static readonly TimeSpan AdoptAfter = TimeSpan.FromDays(60);

	private readonly IReadOnlyList<IRule> _rules;
	private readonly TimeProvider _timeProvider;

	/// <summary>
	/// Initializes a new instance using every registered rule and the system clock.
	/// </summary>
	public DependabotTriageService()
		: this(RuleRegistry.Rules, TimeProvider.System)
	{
	}

	/// <summary>
	/// Initializes a new instance over an explicit rule set, for tests.
	/// </summary>
	/// <param name="rules">The rules to consider when deciding coverage.</param>
	public DependabotTriageService(IReadOnlyList<IRule> rules)
		: this(rules, TimeProvider.System)
	{
	}

	/// <summary>
	/// Initializes a new instance over an explicit rule set and clock, for tests.
	/// </summary>
	/// <param name="rules">The rules to consider when deciding coverage.</param>
	/// <param name="timeProvider">The clock the adoption age gate is measured against.</param>
	public DependabotTriageService(IReadOnlyList<IRule> rules, TimeProvider timeProvider)
	{
		_rules = rules;
		_timeProvider = timeProvider;
	}

	/// <summary>
	/// A verdict for every open item, in the order given.
	/// </summary>
	/// <param name="issues">The repository's open issues and pull requests.</param>
	/// <param name="context">The repository, for what it declares.</param>
	/// <param name="ruleResults">The repository's current assessment.</param>
	/// <param name="canRemediate">Whether a rule id has a remediation that could act on it.</param>
	public IReadOnlyList<DependabotTriage> Triage(
		IReadOnlyList<RepositoryIssue> issues,
		RepositoryContext context,
		IReadOnlyList<RuleResult> ruleResults,
		Func<string, bool> canRemediate)
	{
		var packages = PackageReferenceScanner.Scan(context);
		var actionUsages = ActionUsageScanner.Scan(context);

		return [.. issues.Select(issue => Judge(issue, packages, actionUsages, ruleResults, canRemediate))];
	}

	private DependabotTriage Judge(
		RepositoryIssue issue,
		List<PackageVersionReference> packages,
		List<ActionUsage> actionUsages,
		IReadOnlyList<RuleResult> ruleResults,
		Func<string, bool> canRemediate)
	{
		var proposal = DependabotProposalParser.Parse(issue);

		if (proposal is null)
		{
			return new DependabotTriage(
				issue,
				null,
				DependabotVerdict.Unrecognised,
				"Not a readable Dependabot version bump, so triage leaves it alone.",
				null);
		}

		// Bumps already done drop out: a group of three where two are satisfied should be judged on the
		// one that is not.
		var outstanding = proposal.Bumps
			.Where(bump => !IsSatisfied(bump, packages, actionUsages))
			.ToList();

		if (outstanding.Count == 0)
		{
			return new DependabotTriage(
				issue,
				proposal,
				DependabotVerdict.AlreadySatisfied,
				$"{Describe(proposal.Bumps)} already declared at the proposed version or above, so "
					+ "merging this would change nothing.",
				null);
		}

		// Covered before anything else: if a failing rule is already going to move a dependency, letting
		// the rule do it keeps one mechanism responsible for one change, and the estate floors and rule
		// thresholds keep deciding the target version rather than Dependabot.
		var covering = outstanding
			.Select(bump => CoveringRuleId(bump.Dependency, ruleResults, canRemediate))
			.ToList();

		if (covering.TrueForAll(ruleId => ruleId is not null))
		{
			return new DependabotTriage(
				issue,
				proposal,
				DependabotVerdict.ValidCovered,
				$"Still outstanding, and {string.Join(", ", covering.Distinct())} failing with a "
					+ $"remediation that will move {Names(outstanding)} at least this far.",
				covering[0]);
		}

		// Nothing failing will move all of it. Before calling that a gap or an idle rule, ask whether
		// the pull request has simply been waiting too long: a grace period exists to avoid churning on
		// a release published this morning, not to hold a pull request open for four months.
		var age = _timeProvider.GetUtcNow() - issue.CreatedAtUtc;

		if (age >= AdoptAfter && Plan(outstanding, packages, actionUsages) is { HasAnything: true } plan)
		{
			return new DependabotTriage(
				issue,
				proposal,
				DependabotVerdict.Adoptable,
				$"Open for {age.Days} days with nothing queued to move it, so adopting what it proposes "
					+ $"for {Names(outstanding)} into the local clone.",
				null,
				Adoption: plan);
		}

		// Whether this is a gap in the rule set or a rule that has nothing to say today is a different
		// question, and only the first is anybody's work.
		var gaps = outstanding
			.Where(bump => IsGap(bump, packages, actionUsages, canRemediate))
			.ToList();

		if (gaps.Count > 0)
		{
			return new DependabotTriage(
				issue,
				proposal,
				DependabotVerdict.ValidUncovered,
				$"Still outstanding, and nothing here can ever move {Names(gaps)} — no rule governs "
					+ "it, or the rule that claims it never reads where it is declared.",
				null,
				IsRuleSetGap: true,
				GapBumpsOrNull: gaps);
		}

		var idle = outstanding
			.Where((_, index) => covering[index] is null)
			.ToList();

		return new DependabotTriage(
			issue,
			proposal,
			DependabotVerdict.ValidUncovered,
			$"Still outstanding, and {Describe(idle)} governed by a rule that is not failing for it at "
				+ "the moment, so nothing is queued to move it right now.",
			null);
	}

	/// <summary>
	/// What writing every one of these bumps would take, or null when any of them cannot be written.
	/// </summary>
	/// <remarks>
	/// All-or-nothing, deliberately. A pull request is adopted only when every outstanding bump in it
	/// can be written: adopting part of a group and closing it silently drops the rest, and adopting
	/// part without closing leaves a pull request whose content is mostly already applied — noise on
	/// every subsequent pass. "Closed" has to keep meaning "fully superseded".
	/// </remarks>
	/// <param name="bumps">The outstanding bumps to write.</param>
	/// <param name="packages">Every package declaration the repository makes.</param>
	/// <param name="actionUsages">Every action usage the repository makes.</param>
	private static DependabotAdoptionPlan? Plan(
		IReadOnlyList<DependabotBump> bumps,
		List<PackageVersionReference> packages,
		List<ActionUsage> actionUsages)
	{
		var packageUpdates = new List<string>();
		var patterns = new List<string>();
		var replacements = new List<string>();

		foreach (var bump in bumps)
		{
			switch (bump.Dependency.Ecosystem)
			{
				case DependencyEcosystem.NuGet:
					if (!NuGetVersion.TryParse(bump.ToVersion, out _))
					{
						return null;
					}

					var declarations = packages
						.Where(p => string.Equals(
							p.PackageId, bump.Dependency.Name, StringComparison.OrdinalIgnoreCase))
						.ToList();

					// Declared nowhere the scanner reads means there is nothing to rewrite, which is not
					// the same as nothing to do — so the pull request is not adoptable rather than
					// adoptable-with-no-work.
					if (declarations.Count == 0)
					{
						return null;
					}

					// One entry per declaration site: a package pinned in two project files has to move in
					// both, or the next pass finds it still unsatisfied.
					packageUpdates.AddRange(declarations.Select(d => string.Join(
						'|',
						d.FilePath,
						d.PackageId,
						d.VersionKind,
						d.CurrentVersion,
						bump.ToVersion)));

					break;

				case DependencyEcosystem.GitHubActions:
					if (MajorOf(bump.ToVersion) is null
						|| !actionUsages.Any(u => string.Equals(
							u.Action, bump.Dependency.Name, StringComparison.OrdinalIgnoreCase)))
					{
						return null;
					}

					patterns.Add(ActionUsesPattern.Below(bump.Dependency.Name, bump.ToVersion));
					replacements.Add(ActionUsesPattern.Replacement(bump.ToVersion));

					break;

				default:
					return null;
			}
		}

		return new DependabotAdoptionPlan(packageUpdates, patterns, replacements);
	}

	/// <summary>
	/// Whether nothing here can ever move this bump: no rule governs it, or one claims it but never
	/// reads where it is declared.
	/// </summary>
	private bool IsGap(
		DependabotBump bump,
		List<PackageVersionReference> packages,
		List<ActionUsage> actionUsages,
		Func<string, bool> canRemediate)
		=> GoverningRuleId(bump.Dependency, canRemediate) is null
			|| !IsObserved(bump.Dependency, packages, actionUsages);

	/// <summary>
	/// Names bumps for a sentence a human reads: "Serilog", "Serilog and Refit", or "Serilog, Refit
	/// and 2 others".
	/// </summary>
	/// <remarks>
	/// Every reason sentence names which dependency drove the verdict. A grouped pull request reported
	/// as a gap with no indication of <em>which</em> of its three dependencies is the gap is a sentence
	/// that sends somebody back to GitHub to find out.
	/// </remarks>
	private static string Names(IReadOnlyList<DependabotBump> bumps)
		=> bumps.Count switch
		{
			0 => "nothing",
			1 => bumps[0].Dependency.Name,
			2 => $"{bumps[0].Dependency.Name} and {bumps[1].Dependency.Name}",
			_ => string.Join(", ", bumps.Take(2).Select(b => b.Dependency.Name))
				+ $" and {bumps.Count - 2} other{(bumps.Count == 3 ? string.Empty : "s")}"
		};

	/// <summary>
	/// <see cref="Names"/> with the verb that agrees with it, for a sentence that needs one.
	/// </summary>
	private static string Describe(IReadOnlyList<DependabotBump> bumps)
		=> $"{Names(bumps)} {(bumps.Count == 1 ? "is" : "are")}";

	/// <summary>
	/// Whether the repository already declares the target version or better, everywhere it declares
	/// the dependency at all.
	/// </summary>
	/// <remarks>
	/// Every declaration has to satisfy it, and there has to be at least one. A dependency declared
	/// nowhere is unprovable rather than satisfied: "we could not find it" must never close a pull
	/// request.
	/// </remarks>
	private static bool IsSatisfied(
		DependabotBump bump,
		List<PackageVersionReference> packages,
		List<ActionUsage> actionUsages)
		=> bump.Dependency.Ecosystem switch
		{
			DependencyEcosystem.NuGet => IsPackageSatisfied(bump, packages),
			DependencyEcosystem.GitHubActions => IsActionSatisfied(bump, actionUsages),
			_ => false
		};

	private static bool IsPackageSatisfied(
		DependabotBump bump,
		List<PackageVersionReference> packages)
	{
		if (!NuGetVersion.TryParse(bump.ToVersion, out var target))
		{
			return false;
		}

		var declared = packages
			.Where(p => string.Equals(p.PackageId, bump.Dependency.Name, StringComparison.OrdinalIgnoreCase))
			.Select(p => NuGetVersion.TryParse(p.CurrentVersion, out var version) ? version : null)
			.ToList();

		return declared.Count > 0
			&& declared.All(version => version is not null && version >= target);
	}

	private static bool IsActionSatisfied(DependabotBump bump, List<ActionUsage> actionUsages)
	{
		var target = MajorOf(bump.ToVersion);
		var lowest = ActionUsageScanner.LowestMajorOf(actionUsages, bump.Dependency.Name);

		return target is not null && lowest is not null && lowest >= target;
	}

	/// <summary>
	/// The major version a Dependabot target names, or null when it is not readable as one.
	/// </summary>
	private static int? MajorOf(string version)
		=> NuGetVersion.TryParse(version, out var parsed)
			? parsed.Major
			: int.TryParse(version, out var major) ? major : null;

	/// <summary>
	/// The id of a failing rule that governs this dependency and has a remediation, or null.
	/// </summary>
	/// <remarks>
	/// The rule has to be <em>failing</em>: a passing rule will not be remediated, so it will not move
	/// anything, so it cannot cover a pull request that is still outstanding.
	/// </remarks>
	private string? CoveringRuleId(
		DependencyRef dependency,
		IReadOnlyList<RuleResult> ruleResults,
		Func<string, bool> canRemediate)
	{
		var failing = ruleResults
			.Where(r => !r.Passed)
			.GroupBy(r => r.RuleId, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

		return _rules
			.Where(rule => failing.ContainsKey(rule.RuleId))
			.OfType<IGovernsDependency>()
			.Cast<IRule>()
			.FirstOrDefault(rule =>
				((IGovernsDependency)rule).WillMove(failing[rule.RuleId], dependency)
				&& canRemediate(rule.RuleId))
			?.RuleId;
	}

	/// <summary>
	/// The id of a rule that governs this dependency and has a remediation, whether or not it is
	/// currently failing, or null when nothing governs it.
	/// </summary>
	/// <remarks>
	/// The counterpart to <see cref="CoveringRuleId"/>: that one answers "is a fix coming for this",
	/// this one answers "is this anybody's job". A dependency with an answer here is not a gap in the
	/// rule set, however long its rule stays green.
	/// </remarks>
	private string? GoverningRuleId(DependencyRef dependency, Func<string, bool> canRemediate)
		=> _rules
			.OfType<IGovernsDependency>()
			.Cast<IRule>()
			.FirstOrDefault(rule =>
				((IGovernsDependency)rule).Governs(dependency)
				&& canRemediate(rule.RuleId))
			?.RuleId;

	/// <summary>
	/// Whether the repository declares this dependency anywhere the scanners read.
	/// </summary>
	/// <remarks>
	/// A rule can only fail for what it can see. <c>nbgv</c> is claimed by the package rules - they
	/// claim every NuGet package - but is declared in <c>.config/dotnet-tools.json</c>, which
	/// <see cref="PackageReferenceScanner"/> does not read, so no failure of theirs can ever name it.
	/// Governed but unobserved is a gap, and a more durable one than an ungoverned dependency: the
	/// rule that claims it will never fail for it, so nothing will surface it on its own.
	/// </remarks>
	private static bool IsObserved(
		DependencyRef dependency,
		List<PackageVersionReference> packages,
		List<ActionUsage> actionUsages)
		=> dependency.Ecosystem switch
		{
			DependencyEcosystem.NuGet => packages.Any(p => string.Equals(
				p.PackageId, dependency.Name, StringComparison.OrdinalIgnoreCase)),
			DependencyEcosystem.GitHubActions => actionUsages.Any(u => string.Equals(
				u.Action, dependency.Name, StringComparison.OrdinalIgnoreCase)),
			_ => false
		};

}
