namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// What the last test run in a repository's working tree did.
/// </summary>
/// <remarks>
/// Mirrors <see cref="RepositoryBuildState"/>, and for the same reason has no member for "not known":
/// that is the null on the row. A repository that has never been tested here, and one whose tree has
/// changed since, are the same thing to a reader, and neither is a pass.
/// </remarks>
public enum RepositoryTestState
{
	/// <summary>The tests passed.</summary>
	Passed,

	/// <summary>The tests failed, or could not be run.</summary>
	Failed
}
