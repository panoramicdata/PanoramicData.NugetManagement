namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// A package we publish whose source is not a repository we govern, and why.
/// </summary>
/// <remarks>
/// Kept out of the repository rows rather than shoehorned into one. A row that stands for a
/// repository and holds no repository is a contradiction every consumer then has to guard against;
/// these have their own list and their own branch of the tree, counted in nothing.
/// </remarks>
public class UngovernedPackage
{
	/// <summary>The NuGet package identifier.</summary>
	public required string PackageId { get; init; }

	/// <summary>The organisation the package was discovered under.</summary>
	public string Organization { get; init; } = string.Empty;

	/// <summary>The repository the nuspec declared, when it declared one we do not govern.</summary>
	public string? DeclaredRepository { get; init; }

	/// <summary>Why this package is not governed. Names the nuspec that needs correcting.</summary>
	public required string Reason { get; init; }
}
