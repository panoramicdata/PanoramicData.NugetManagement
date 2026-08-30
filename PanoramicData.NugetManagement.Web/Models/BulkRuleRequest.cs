namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>What a bulk rule action asks the host to queue, once fanned out across its repositories.</summary>
/// <param name="Organization">The organisation the repositories belong to.</param>
/// <param name="RuleId">
/// The rule to apply, or null for a trailing commit-and-push queued once every rule in a multi-rule
/// sweep (a category or "fix everything") has already been fanned out onto its repositories. A sweep
/// pushes once per repository rather than once per rule — see <see cref="Push"/> — so this is how it
/// asks the host to queue that final push once every rule's fixes are in the lane ahead of it.
/// </param>
/// <param name="RepositoryFullNames">The repositories it affects.</param>
/// <param name="Push">
/// Whether to commit and push each repository after fixing it. True for a single-rule apply, where
/// one fix is one push; false for each rule in a multi-rule sweep, whose repositories instead get one
/// trailing request with a null <see cref="RuleId"/> — otherwise a twelve-rule sweep across forty
/// repositories would commit and push up to 480 times instead of 40.
/// </param>
/// <param name="Title">What to say in the console.</param>
public sealed record BulkRuleRequest(
	string Organization,
	string? RuleId,
	IReadOnlyList<string> RepositoryFullNames,
	bool Push,
	string Title);
