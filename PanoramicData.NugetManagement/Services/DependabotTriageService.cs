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
public sealed record DependabotTriage(
	RepositoryIssue Issue,
	DependabotProposal? Proposal,
	DependabotVerdict Verdict,
	string Reason,
	string? CoveringRuleId);

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

		return coveringRuleId is null
			? new DependabotTriage(
				issue,
				proposal,
				DependabotVerdict.ValidUncovered,
				$"Still outstanding, and no rule enforces a minimum version of "
					+ $"{proposal.Dependency.Name}, so nothing here can fix it automatically.",
				null)
			: new DependabotTriage(
				issue,
				proposal,
				DependabotVerdict.ValidCovered,
				$"Still outstanding, and {coveringRuleId} is failing with a remediation that will move "
					+ $"{proposal.Dependency.Name} at least this far.",
				coveringRuleId);
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
				((IGovernsDependency)rule).Governs(dependency)
				&& WillMove(failing[rule.RuleId], dependency)
				&& canRemediate(rule.RuleId))
			?.RuleId;
	}

	/// <summary>
	/// Whether this particular failure will move this particular dependency.
	/// </summary>
	/// <remarks>
	/// A rule that claims a whole ecosystem — CI-12 claims every action no other rule owns — is still
	/// only going to fix what it found wrong. Its failure names those in <c>governed_actions</c>, and
	/// a dependency missing from that list is not covered by it, however broadly it governs. Rules
	/// that do not narrow their claim carry no such key and are unaffected.
	/// </remarks>
	private static bool WillMove(RuleResult failure, DependencyRef dependency)
		=> failure.Advisory?.Data.TryGetValue("governed_actions", out var named) is not true
			|| named is not IEnumerable<string> names
			|| names.Contains(dependency.Name, StringComparer.OrdinalIgnoreCase);
}
