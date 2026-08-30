namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// What a queued item will do, in a form that can be written to disk and read back.
/// </summary>
/// <param name="Kind">Which work this is.</param>
/// <param name="Organization">The organisation it belongs to, or null when it spans all of them.</param>
/// <param name="RepositoryFullName">The repository it acts on as "owner/name", or null for organisation-scoped work.</param>
/// <param name="Parameters">The few extras a kind needs, such as <c>ruleId</c> or <c>category</c>.</param>
public sealed record WorkDescriptor(
	WorkKind Kind,
	string? Organization,
	string? RepositoryFullName,
	IReadOnlyDictionary<string, string> Parameters)
{
	/// <summary>
	/// The lane this work runs on: its repository's, or its organisation's when it acts on no single
	/// repository. Lower-cased, because a repository named two ways is still one working tree and
	/// must not end up with two lanes running against it at once.
	/// </summary>
	public string LaneKey => RepositoryFullName is { Length: > 0 } repository
		? $"repo:{repository.ToLowerInvariant()}"
		: $"org:{(Organization ?? "*").ToLowerInvariant()}";

	/// <summary>The named parameter's value, or null when this kind does not carry it.</summary>
	/// <param name="name">The parameter name, e.g. <c>ruleId</c>.</param>
	public string? Parameter(string name)
		=> Parameters.TryGetValue(name, out var value) ? value : null;

	/// <summary>Describes work acting on one repository.</summary>
	public static WorkDescriptor ForRepository(
		WorkKind kind,
		string? organization,
		string repositoryFullName,
		params (string Name, string Value)[] parameters)
		=> new(kind, organization, repositoryFullName, ToDictionary(parameters));

	/// <summary>Describes work acting on an organisation rather than any one repository.</summary>
	public static WorkDescriptor ForOrganization(
		WorkKind kind,
		string? organization,
		params (string Name, string Value)[] parameters)
		=> new(kind, organization, null, ToDictionary(parameters));

	private static Dictionary<string, string> ToDictionary((string Name, string Value)[] parameters)
		=> parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
}
