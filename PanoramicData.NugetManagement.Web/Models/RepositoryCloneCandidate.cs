namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// A repository offered for cloning in the clone-repositories dialog. Built from the organisation's
/// repositories as GitHub reports them, so visibility is known — which the dashboard rows cannot
/// tell us, since those are discovered from nuget.org package metadata.
/// </summary>
public class RepositoryCloneCandidate
{
	/// <summary>
	/// The repository name, which is also the folder it clones into.
	/// </summary>
	public required string Name { get; init; }

	/// <summary>
	/// The owner-qualified name, e.g. "panoramicdata/Highlight.Api".
	/// </summary>
	public required string FullName { get; init; }

	/// <summary>
	/// The URL to clone from.
	/// </summary>
	public required string CloneUrl { get; init; }

	/// <summary>
	/// Whether GitHub reports the repository as private. Private repositories are offered but not
	/// selected by default: the action is "clone the public repositories", and taking a private one
	/// locally is a deliberate choice.
	/// </summary>
	public bool IsPrivate { get; init; }

	/// <summary>
	/// Whether the repository is archived. Also offered but not selected by default, since archived
	/// repositories are rarely wanted for local work.
	/// </summary>
	public bool IsArchived { get; init; }

	/// <summary>
	/// Whether this repository backs a package the dashboard knows about. Repositories that publish
	/// nothing are still listed — they are part of the organisation — but this distinguishes them.
	/// </summary>
	public bool HasPackage { get; init; }

	/// <summary>
	/// Whether the user has this repository selected for cloning.
	/// </summary>
	public bool Selected { get; set; }
}
