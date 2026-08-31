using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Turns a repository's open Dependabot pull requests into findings the issue-centric tree can group.
/// </summary>
/// <remarks>
/// The issue dimension groups failing <see cref="RuleResult"/>s by rule id, so anything that wants to
/// appear there — and to inherit its bulk action across every affected repository — has to arrive as
/// one. A real rule cannot do this job: rules are evaluated against a
/// <see cref="RepositoryContext"/>, which carries the repository's files and knows nothing about its
/// GitHub inbox. So the findings are synthesised here, in the layer that holds the rows.
/// <para>
/// One finding per <em>dependency</em>, with a rule id derived from the dependency itself, so the same
/// dependency in twelve repositories collapses to one node with twelve occurrences — and clearing
/// `actions/checkout` everywhere is one action rather than twelve.
/// </para>
/// <para>
/// These findings are not remediable in the ordinary sense: <see cref="Remediations.IRemediation"/> is
/// synchronous and writes to the filesystem, and closing a pull request is neither. Their bulk action
/// queues <see cref="WorkKind.TriageDependabot"/> instead, which is why the rule id is recognisable
/// via <see cref="IsSynthetic"/>.
/// </para>
/// </remarks>
public static class DependabotIssueSynthesizer
{
	/// <summary>
	/// The prefix marking a rule id as one of these synthesised findings rather than a real rule.
	/// </summary>
	public const string RuleIdPrefix = "DEPBOT:";

	/// <summary>
	/// Whether a rule id is one of these findings, and so whether Fix means triage rather than
	/// applying a remediation.
	/// </summary>
	/// <param name="ruleId">The rule id to test.</param>
	public static bool IsSynthetic(string? ruleId)
		=> ruleId?.StartsWith(RuleIdPrefix, StringComparison.OrdinalIgnoreCase) == true;

	/// <summary>
	/// The repository's assessment with these findings added.
	/// </summary>
	/// <param name="row">The repository.</param>
	/// <remarks>
	/// A new assessment rather than an edit to the stored one. Rendering happens repeatedly, and
	/// appending to the cached assessment would add the same findings again on every pass.
	/// </remarks>
	public static RepoAssessment Augment(RepositoryDashboardRow row)
	{
		var assessment = row.Assessment
			?? throw new ArgumentException("The row has no assessment to augment.", nameof(row));

		var synthesised = Synthesize(row);

		return synthesised.Count == 0
			? assessment
			: new RepoAssessment
			{
				RepositoryFullName = assessment.RepositoryFullName,
				DefaultBranch = assessment.DefaultBranch,
				AssessedAtUtc = assessment.AssessedAtUtc,
				RuleResults = [.. assessment.RuleResults, .. synthesised]
			};
	}

	/// <summary>
	/// One finding per dependency Dependabot has an open pull request for.
	/// </summary>
	/// <param name="row">The repository.</param>
	public static IReadOnlyList<RuleResult> Synthesize(RepositoryDashboardRow row)
	{
		// An unread inbox is not an empty one. Synthesising from it would report every repository as
		// having no Dependabot work, which is indistinguishable from being up to date.
		if (!row.OpenIssuesKnown)
		{
			return [];
		}

		var nowUtc = DateTimeOffset.UtcNow;

		var proposals = row.OpenIssues
			.Select(issue => (Issue: issue, Proposal: DependabotTitleParser.Parse(issue)))
			.Where(pair => pair.Proposal is not null)
			.ToList();

		return
		[
			.. proposals
				.GroupBy(pair => pair.Proposal!.Dependency)
				.Select(group => Finding(group.Key, [.. group], nowUtc))
				.OrderBy(result => result.RuleId, StringComparer.OrdinalIgnoreCase)
		];
	}

	private static RuleResult Finding(
		DependencyRef dependency,
		IReadOnlyList<(RepositoryIssue Issue, DependabotProposal? Proposal)> pairs,
		DateTimeOffset nowUtc)
		=> new()
		{
			RuleId = $"{RuleIdPrefix}{Slug(dependency.Ecosystem)}/{dependency.Name.ToLowerInvariant()}",
			RuleName = $"Dependabot: {dependency.Name}",
			Category = AssessmentCategory.DependencyAutomation,

			// The worst staleness among the pull requests behind it, so the tree orders these the way it
			// orders everything else: by how long somebody has been waiting.
			Severity = pairs.Max(pair => pair.Issue.SeverityAt(nowUtc)),
			Passed = false,
			Message = Describe(dependency, pairs)
		};

	private static string Describe(
		DependencyRef dependency,
		IReadOnlyList<(RepositoryIssue Issue, DependabotProposal? Proposal)> pairs)
	{
		var parts = pairs
			.OrderBy(pair => pair.Issue.Number)
			.Select(pair =>
				$"#{pair.Issue.Number} ({pair.Proposal!.FromVersion} → {pair.Proposal.ToVersion})"
				+ Verdict(pair.Issue));

		return $"{dependency.Name}: {string.Join(", ", parts)}.";
	}

	/// <summary>
	/// What the last triage pass concluded, where it has run. Left off entirely where it has not,
	/// rather than saying "not triaged" against every pull request in the estate.
	/// </summary>
	private static string Verdict(RepositoryIssue issue) => issue.TriageVerdict switch
	{
		DependabotVerdict.AlreadySatisfied => " — superseded, closeable",
		DependabotVerdict.ValidCovered => " — an auto-fix covers it",
		DependabotVerdict.ValidUncovered => " — valid, no auto-fix",
		DependabotVerdict.Unrecognised => " — left alone",
		_ => string.Empty
	};

	private static string Slug(DependencyEcosystem ecosystem) => ecosystem switch
	{
		DependencyEcosystem.GitHubActions => "github-actions",
		DependencyEcosystem.NuGet => "nuget",
		_ => "unknown"
	};
}
