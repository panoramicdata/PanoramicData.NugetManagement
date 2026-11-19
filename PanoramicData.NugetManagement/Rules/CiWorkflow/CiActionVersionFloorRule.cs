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

	/// <summary>
	/// The advisory key naming the actions a failure of this rule will move.
	/// </summary>
	private const string _governedActionsKey = "governed_actions";


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

	/// <inheritdoc />
	/// <remarks>
	/// This rule claims a whole ecosystem but only moves what it found behind, and the failure names
	/// those in <c>governed_actions</c>. An action missing from that list is not moved by this failure,
	/// however broadly the rule governs.
	/// </remarks>
	public bool WillMove(RuleResult failure, DependencyRef dependency)
		=> Governs(dependency)
			&& AdvisoryNames.Contains(failure, _governedActionsKey, dependency.Name);

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

		var (behind, checkedCount) = FindBehind(context, usages);
		if (checkedCount == 0)
		{
			// Every action present belongs to a bespoke rule, which decides compliance, so this one has nothing to judge.
			return Task.FromResult(NotApplicable(
				"Every action used is one another rule already holds to a version."));
		}

		return Task.FromResult(behind.Count == 0
			? Pass($"{checkedCount} action(s) are at or above the versions we use elsewhere.")
			: FailBehind(behind));
	}

	private static (List<(string Action, int Used, string Floor)> Behind, int CheckedCount) FindBehind(
		RepositoryContext context,
		List<ActionUsage> usages)
	{
		var catalog = ActionVersionCatalog.Default;
		var behind = new List<(string Action, int Used, string Floor)>();
		var checkedCount = 0;

		// The one file the bespoke CI rules read; everything outside it is ours, even for a claimed action.
		var ownedWorkflow = Normalise(CiWorkflowPathResolver.Resolve(context));

		var actions = usages
			.Select(usage => usage.Action)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(action => action, StringComparer.OrdinalIgnoreCase);

		foreach (var action in actions)
		{
			var (counted, found) = AssessAction(context, catalog, usages, ownedWorkflow, action);
			checkedCount += counted ? 1 : 0;
			if (found is { } entry)
			{
				behind.Add(entry);
			}
		}

		return (behind, checkedCount);
	}

	private static (bool Counted, (string Action, int Used, string Floor)? Behind) AssessAction(
		RepositoryContext context,
		ActionVersionCatalog catalog,
		List<ActionUsage> usages,
		string ownedWorkflow,
		string action)
	{
		// A claimed action is its owning rule's business in the file that rule reads, and ours in every other workflow.
		var relevant = ClaimedElsewhere(action)
			? usages.Where(usage => !string.Equals(
				Normalise(usage.WorkflowPath), ownedWorkflow, StringComparison.OrdinalIgnoreCase)).ToList()
			: usages;

		if (!relevant.Any(usage => Matches(usage, action)))
		{
			return (false, null);
		}

		// Learning takes the highest usage; failing takes the lowest, because one workflow left behind is still work.
		var highest = relevant
			.Where(usage => Matches(usage, action))
			.Select(usage => usage.MajorVersion)
			.Where(major => major is not null)
			.Max();

		if (highest is not null)
		{
			catalog.Observe(action, highest.Value, _noHardcodedFloor, context.FullName);
		}

		// Null when any usage is unreadable (a SHA pin or a branch): rewriting one would replace a deliberate pin.
		var lowest = ActionUsageScanner.LowestMajorOf(relevant, action);
		if (lowest is null)
		{
			return (true, null);
		}

		var floor = catalog.GetFloorSpec(action, _noHardcodedFloor);
		return lowest >= GitHubActionVersion.ParseMajor(floor)
			? (true, null)
			: (true, (action, lowest.Value, floor));
	}

	private RuleResult FailBehind(List<(string Action, int Used, string Floor)> behind)
		=> Fail(
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
					["globs"] = ActionUsesPattern.WorkflowGlobs,
					["patterns"] = behind.Select(b => ActionUsesPattern.Below(b.Action, b.Floor)).ToArray(),
					["replacements"] = behind.Select(b => ActionUsesPattern.Replacement(b.Floor)).ToArray(),
					[_governedActionsKey] = behind.Select(b => b.Action).ToArray()
				}
			});

	private static bool Matches(ActionUsage usage, string action)
		=> string.Equals(usage.Action, action, StringComparison.OrdinalIgnoreCase);

	/// <summary>Path comparison that does not care which slash a context happened to use.</summary>
	private static string Normalise(string path) => path.Replace('\\', '/');

}
