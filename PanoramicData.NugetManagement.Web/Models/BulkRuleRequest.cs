namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>What a bulk rule action asks the host to queue, once fanned out across its repositories.</summary>
/// <param name="Organization">The organisation the repositories belong to.</param>
/// <param name="RuleId">The rule to apply.</param>
/// <param name="RepositoryFullNames">The repositories it affects.</param>
/// <param name="Push">Whether to commit and push each repository after fixing it.</param>
/// <param name="Title">What to say in the console.</param>
public sealed record BulkRuleRequest(
	string Organization,
	string RuleId,
	IReadOnlyList<string> RepositoryFullNames,
	bool Push,
	string Title);
