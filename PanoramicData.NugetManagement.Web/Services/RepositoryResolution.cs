namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// What came of asking where a package's source lives.
/// </summary>
public enum RepositoryResolutionOutcome
{
	/// <summary>The package names a GitHub repository.</summary>
	Resolved,

	/// <summary>The nuspec was read and names no GitHub repository.</summary>
	NotDeclared,

	/// <summary>The nuspec could not be read, so nothing is known either way.</summary>
	LookupFailed
}

/// <summary>
/// Where a package's source lives, or why we cannot say.
/// </summary>
/// <remarks>
/// The distinction between <see cref="RepositoryResolutionOutcome.NotDeclared"/> and
/// <see cref="RepositoryResolutionOutcome.LookupFailed"/> is the whole point of this type. Both were
/// once a null string, so a dropped connection was recorded as a fact about somebody's nuspec, and
/// eight repositories that declare themselves perfectly well were reported as declaring nothing.
/// </remarks>
public sealed class RepositoryResolution
{
	private RepositoryResolution(RepositoryResolutionOutcome outcome, string? repositoryUrl, string? error)
	{
		Outcome = outcome;
		RepositoryUrl = repositoryUrl;
		Error = error;
	}

	/// <summary>What came of the lookup.</summary>
	public RepositoryResolutionOutcome Outcome { get; }

	/// <summary>The canonical repository URL, set only when <see cref="Outcome"/> is Resolved.</summary>
	public string? RepositoryUrl { get; }

	/// <summary>Why the lookup failed, set only when <see cref="Outcome"/> is LookupFailed.</summary>
	public string? Error { get; }

	/// <summary>The package names the given GitHub repository.</summary>
	/// <param name="repositoryUrl">The canonical repository URL.</param>
	public static RepositoryResolution Resolved(string repositoryUrl)
		=> new(RepositoryResolutionOutcome.Resolved, repositoryUrl, null);

	/// <summary>The nuspec was read and names no GitHub repository.</summary>
	public static RepositoryResolution NotDeclared()
		=> new(RepositoryResolutionOutcome.NotDeclared, null, null);

	/// <summary>The nuspec could not be read.</summary>
	/// <param name="error">Why the lookup failed.</param>
	public static RepositoryResolution LookupFailed(string error)
		=> new(RepositoryResolutionOutcome.LookupFailed, null, error);
}
