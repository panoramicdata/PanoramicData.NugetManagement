using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Holds every GitHub Action in every workflow to the best version the organization uses anywhere.
/// </summary>
/// <remarks>
/// <para>
/// The bespoke rules — CI-05 for <c>actions/checkout</c>, CI-06 for <c>actions/setup-dotnet</c>,
/// CI-08 for the artifact actions — each know one action and check one file. This knows none of them
/// and reads all of the workflows, so an action nobody has written a rule for is still held to a
/// standard. Anything another rule already claims is left to it: two rules failing over one action
/// would double the entry in the fix list and make Dependabot triage's choice of covering rule depend
/// on registry order.
/// </para>
/// <para>
/// The floor comes entirely from <see cref="ActionVersionCatalog"/> with no hardcoded starting
/// version, so it is exactly "the highest version any of our repositories uses". An action only one
/// repository uses therefore floors at its own version and passes, which is the honest answer: with
/// nothing to compare against, nothing can be shown to be behind.
/// </para>
/// </remarks>
public class CiActionVersionFloorRule : RuleBase, IGovernsDependency
{
	private const string _ruleId = "CI-12";

	/// <summary>
	/// Passed where the bespoke rules pass a hardcoded "latest", so the floor is purely learned.
	/// </summary>
	private const string _noHardcodedFloor = "v0";

	private static readonly string[] _workflowGlobs = [".github/workflows/*.yml", ".github/workflows/*.yaml"];

	/// <inheritdoc />
	public override string RuleId => _ruleId;

	/// <inheritdoc />
	public override string RuleName => "Actions are at the versions we use elsewhere";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CiCd;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	/// <remarks>
	/// Claims every action no other rule does. Triage narrows this further using the
	/// <c>governed_actions</c> the failing result carries: this rule only moves the actions it found
	/// behind, and claiming the rest would report a pull request as covered by a fix that never
	/// touches it.
	/// </remarks>
	public bool Governs(DependencyRef dependency)
		=> dependency.Ecosystem == DependencyEcosystem.GitHubActions
			&& !ClaimedElsewhere(dependency.Name);

	/// <summary>
	/// Whether some other rule already enforces a minimum version of this action.
	/// </summary>
	private static bool ClaimedElsewhere(string action)
	{
		var dependency = new DependencyRef(DependencyEcosystem.GitHubActions, action);

		return RuleRegistry.Rules
			.Where(rule => !string.Equals(rule.RuleId, _ruleId, StringComparison.OrdinalIgnoreCase))
			.OfType<IGovernsDependency>()
			.Any(rule => rule.Governs(dependency));
	}

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var usages = ActionUsageScanner.Scan(context);
		if (usages.Count == 0)
		{
			return Task.FromResult(NotApplicable("No workflow uses a versioned action."));
		}

		var catalog = ActionVersionCatalog.Default;
		var behind = new List<(string Action, int Used, string Floor)>();
		var checkedCount = 0;

		// The one file the bespoke CI rules read. Everything outside it is nobody's but ours, even for
		// an action one of them claims.
		var ownedWorkflow = Normalise(CiWorkflowPathResolver.Resolve(context));

		var actions = usages
			.Select(usage => usage.Action)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(action => action, StringComparer.OrdinalIgnoreCase);

		foreach (var action in actions)
		{
			// A claimed action is its owning rule's business in the file that rule reads, and ours in
			// every other workflow — where that rule cannot see it, cannot fail for it, and cannot fix
			// it. Skipping the action outright is what left actions/checkout@v3 sitting in a repository's
			// codeql-analysis.yml with two Dependabot pull requests open against it for months.
			// Note that the remediation's globs still cover every workflow, so fixing a claimed action
			// outside ci.yml also lifts ci.yml's copy of it. That is deliberate and safe: the pattern
			// only matches versions below this floor, so it can raise a version but never lower one,
			// and the owning rule's floor is the greater of its hardcoded default and the same learned
			// value — so if it wants more, it still fails and takes it the rest of the way.
			var relevant = ClaimedElsewhere(action)
				? usages.Where(usage => !string.Equals(
					Normalise(usage.WorkflowPath), ownedWorkflow, StringComparison.OrdinalIgnoreCase)).ToList()
				: usages;

			if (!relevant.Any(usage => Matches(usage, action)))
			{
				continue;
			}

			checkedCount++;

			// Learning takes the highest usage — the best we manage anywhere is the standard to hold
			// everyone to. Failing takes the lowest, because one workflow left behind is still work.
			var highest = relevant
				.Where(usage => Matches(usage, action))
				.Select(usage => usage.MajorVersion)
				.Where(major => major is not null)
				.Max();

			if (highest is not null)
			{
				catalog.Observe(action, highest.Value, _noHardcodedFloor, context.FullName);
			}

			// Null when any usage is unreadable — a SHA pin or a branch. Rewriting one of those would
			// replace a deliberate pin with a floating tag, so the action is left alone entirely.
			var lowest = ActionUsageScanner.LowestMajorOf(relevant, action);
			if (lowest is null)
			{
				continue;
			}

			var floor = catalog.GetFloorSpec(action, _noHardcodedFloor);
			if (lowest >= GitHubActionVersion.ParseMajor(floor))
			{
				continue;
			}

			behind.Add((action, lowest.Value, floor));
		}

		if (checkedCount == 0)
		{
			// Every action present belongs to a bespoke rule, so this one has nothing to judge. Saying
			// so is not the same as saying the repository is compliant — the owning rules decide that.
			return Task.FromResult(NotApplicable(
				"Every action used is one another rule already holds to a version."));
		}

		if (behind.Count == 0)
		{
			return Task.FromResult(Pass(
				$"{checkedCount} action(s) are at or above the versions we use elsewhere."));
		}

		return Task.FromResult(Fail(
			string.Join("; ", behind.Select(b => $"{b.Action}@v{b.Used} is behind {b.Floor}")) + ".",
			new RuleAdvisory
			{
				Summary = "Update " + string.Join(", ", behind.Select(b => $"{b.Action} to {b.Floor}")),
				Detail = "Update these actions to the versions used elsewhere in the organization: "
					+ string.Join(", ", behind.Select(b => $"`{b.Action}@{b.Floor}`"))
					+ ". Every `uses:` line in every workflow is rewritten, sub-actions included.",
				Data = new()
				{
					["remediation_type"] = "replace_regex_in_files",
					["globs"] = _workflowGlobs,
					["patterns"] = behind.Select(b => PatternFor(b.Action, b.Floor)).ToArray(),
					["replacements"] = behind.Select(b => $"${{1}}{b.Floor}").ToArray(),
					["governed_actions"] = behind.Select(b => b.Action).ToArray()
				}
			}));
	}

	private static bool Matches(ActionUsage usage, string action)
		=> string.Equals(usage.Action, action, StringComparison.OrdinalIgnoreCase);

	/// <summary>Path comparison that does not care which slash a context happened to use.</summary>
	private static string Normalise(string path) => path.Replace('\\', '/');

	/// <summary>
	/// Matches every pinned version of an action that is <em>below</em> the floor, sub-actions
	/// included: one pattern rewrites <c>github/codeql-action/init@v2</c> and
	/// <c>github/codeql-action/analyze@v2</c> alike, because the sub-actions carry the repository's
	/// version rather than one of their own.
	/// </summary>
	/// <remarks>
	/// The majors below the floor are listed out rather than matched as <c>v\d+</c>, because a pattern
	/// that matches any version rewrites <em>every</em> version — so a workflow already on v9 would be
	/// dragged back to a floor of v7 by a fix aimed at a different workflow on v3. A rule that is
	/// careful to treat being ahead as compliant must not then have a remediation that levels
	/// everything to the average.
	/// <para>
	/// The trailing lookahead is what stops <c>v1</c> matching the first character of <c>v16</c>: with
	/// the alternation alone, a floor of v7 would rewrite v10 and v70 as though they were behind.
	/// </para>
	/// </remarks>
	/// <param name="action">The action to rewrite.</param>
	/// <param name="floorSpec">
	/// The floor, as a spec like <c>v7</c>. Never <c>v0</c> here: a rule that only fails when a usage
	/// is below the floor cannot fail at a floor of zero, so the alternation is never empty.
	/// </param>
	private static string PatternFor(string action, string floorSpec)
	{
		var floor = GitHubActionVersion.ParseMajor(floorSpec);
		var below = string.Join('|', Enumerable.Range(0, floor));

		return $@"({Regex.Escape(action)}(?:/[A-Za-z0-9_.-]+)*@)v(?:{below})(?:\.\d+)*(?![\d.])";
	}
}
