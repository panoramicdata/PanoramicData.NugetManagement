using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// What a work item is doing, in a sentence.
/// </summary>
/// <remarks>
/// The tree node says "Fix with AI"; this says which rule, in which repository, and — for the one
/// kind where it matters — that a model is doing it and will take its time. A closed catalogue, held
/// to the enum by a test, so a new kind cannot ship with nothing to say for itself.
/// </remarks>
public static class WorkDescription
{
	/// <summary>Whether a model does this work, which is what makes its pane a session rather than a log.</summary>
	/// <param name="kind">The kind of work.</param>
	public static bool IsAi(WorkKind kind) => kind is WorkKind.FixWithAiRule;

	/// <summary>Whether the kind has a sentence of its own.</summary>
	/// <param name="kind">The kind of work.</param>
	public static bool IsDescribed(WorkKind kind) => Sentence(kind, "the repository", "the rule", "the category") is not null;

	/// <summary>
	/// One sentence describing what this item will do.
	/// </summary>
	/// <param name="item">The item.</param>
	public static string For(WorkItem item)
	{
		var scope = item.RepositoryFullName ?? item.Organization ?? "every organisation";
		var rule = item.Descriptor.Parameter("ruleId") ?? "a rule";
		var category = item.Descriptor.Parameter("category") ?? "a category";

		return Sentence(item.Descriptor.Kind, scope, rule, category)
			?? $"{item.Descriptor.Kind} on {scope}.";
	}

	/// <summary>
	/// The catalogue. Null for a kind with nothing written for it, which is what
	/// <see cref="IsDescribed"/> reports and a test refuses to allow.
	/// </summary>
	private static string? Sentence(WorkKind kind, string scope, string rule, string category) => kind switch
	{
		WorkKind.Clone => $"Cloning {scope} so its files can be read and changed on disk.",
		WorkKind.Reassess => $"Re-running every governance rule against {scope}.",
		WorkKind.FixAll => $"Applying every deterministic remediation {scope} is failing, then re-assessing it.",
		WorkKind.FixCategory => $"Applying the deterministic remediations for {category} in {scope}, then re-assessing it.",
		WorkKind.FixRule => $"Applying the deterministic remediation for {rule} in {scope}.",
		WorkKind.FixWithAiRule =>
			$"Asking a local model to fix {rule} in {scope}. It reads and edits the clone itself, and the "
				+ "rule is re-checked after each attempt — so this takes minutes rather than seconds, and "
				+ "what follows is the session as it happens.",
		WorkKind.Build => $"Building {scope}.",
		WorkKind.Test => $"Running the tests in {scope}.",
		WorkKind.TriageDependabot => $"Deciding what to do about each open Dependabot pull request in {scope}.",
		WorkKind.GitSync => $"Pulling and pushing {scope} so the clone and the remote agree.",
		WorkKind.CommitAndPush => $"Committing what has changed in {scope} and pushing it.",
		WorkKind.Publish => $"Tagging {scope} and letting CI publish the package.",
		WorkKind.RediscoverOrganization => $"Re-reading {scope} from GitHub: which repositories it has, and what each publishes.",
		WorkKind.DiscoverReassessTargets => $"Working out which repositories in {scope} need re-assessing, then queueing one item for each.",
		WorkKind.DiscoverCloneTargets => $"Working out which repositories in {scope} are not cloned yet, then queueing one item for each.",
		WorkKind.RefreshAll => $"Refreshing everything known about {scope}.",
		_ => null
	};
}
