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
	bool IsRuleSetGap = false);

/// <summary>
/// Decides what to do about each of a repository's open Dependabot pull requests.
/// </summary>
/// <remarks>
/// Pure: no I/O, no GitHub, no clock. Everything it needs is the pull requests, what the repository
/// declares, which rules are failing, and whether a rule has a remediation. That last one arrives as
/// a predicate rather than a dependency on <c>RemediationRegistry</c>, which lives in the web project
/// — keeping the existing rules-in-core, remediations-in-web seam intact.
/// </remarks>
public sealed class DependabotTriageService
{
	private readonly IReadOnlyList<IRule> _rules;

	/// <summary>
	/// Initializes a new instance using every registered rule.
	/// </summary>
	public DependabotTriageService()
		: this(RuleRegistry.Rules)
	{
	}

	/// <summary>
	/// Initializes a new instance over an explicit rule set, for tests.
	/// </summary>
	/// <param name="rules">The rules to consider when deciding coverage.</param>
	public DependabotTriageService(IReadOnlyList<IRule> rules) => _rules = rules;

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
		var proposal = DependabotTitleParser.Parse(issue);

		if (proposal is null)
		{
			return new DependabotTriage(
				issue,
				null,
				DependabotVerdict.Unrecognised,
				"Not a single-dependency Dependabot version bump, so triage leaves it alone.",
				null);
		}

		if (IsSatisfied(proposal, packages, actionUsages))
		{
			return new DependabotTriage(
				issue,
				proposal,
				DependabotVerdict.AlreadySatisfied,
				$"{proposal.Dependency.Name} is already declared at {proposal.ToVersion} or above, so "
					+ "merging this would change nothing.",
				null);
		}

		var coveringRuleId = CoveringRuleId(proposal.Dependency, ruleResults, canRemediate);

		if (coveringRuleId is not null)
		{
			return new DependabotTriage(
				issue,
				proposal,
				DependabotVerdict.ValidCovered,
				$"Still outstanding, and {coveringRuleId} is failing with a remediation that will move "
					+ $"{proposal.Dependency.Name} at least this far.",
				coveringRuleId);
		}

		// Nothing will move it today. Whether that is a gap in the rule set or a rule that has nothing
		// to say today is a different question, and only the first is anybody's work.
		var governingRuleId = GoverningRuleId(proposal.Dependency, canRemediate);

		if (governingRuleId is null)
		{
			return new DependabotTriage(
				issue,
				proposal,
				DependabotVerdict.ValidUncovered,
				$"Still outstanding, and no rule governs {proposal.Dependency.Name} at all, so nothing "
					+ "here can fix it automatically.",
				null,
				IsRuleSetGap: true);
		}

		if (!IsObserved(proposal.Dependency, packages, actionUsages))
		{
			return new DependabotTriage(
				issue,
				proposal,
				DependabotVerdict.ValidUncovered,
				$"Still outstanding, and {governingRuleId} claims {proposal.Dependency.Name} but never "
					+ "reads where it is declared, so no failure of it can ever move this.",
				null,
				IsRuleSetGap: true);
		}

		return new DependabotTriage(
			issue,
			proposal,
			DependabotVerdict.ValidUncovered,
			$"Still outstanding, and {governingRuleId} governs {proposal.Dependency.Name} but is not "
				+ "failing for it at the moment, so nothing is queued to move it right now.",
			null);
	}

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
		DependabotProposal proposal,
		List<PackageVersionReference> packages,
		List<ActionUsage> actionUsages)
		=> proposal.Dependency.Ecosystem switch
		{
			DependencyEcosystem.NuGet => IsPackageSatisfied(proposal, packages),
			DependencyEcosystem.GitHubActions => IsActionSatisfied(proposal, actionUsages),
			_ => false
		};

	private static bool IsPackageSatisfied(
		DependabotProposal proposal,
		List<PackageVersionReference> packages)
	{
		if (!NuGetVersion.TryParse(proposal.ToVersion, out var target))
		{
			return false;
		}

		var declared = packages
			.Where(p => string.Equals(p.PackageId, proposal.Dependency.Name, StringComparison.OrdinalIgnoreCase))
			.Select(p => NuGetVersion.TryParse(p.CurrentVersion, out var version) ? version : null)
			.ToList();

		return declared.Count > 0
			&& declared.All(version => version is not null && version >= target);
	}

	private static bool IsActionSatisfied(DependabotProposal proposal, List<ActionUsage> actionUsages)
	{
		var target = MajorOf(proposal.ToVersion);
		var lowest = ActionUsageScanner.LowestMajorOf(actionUsages, proposal.Dependency.Name);

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
