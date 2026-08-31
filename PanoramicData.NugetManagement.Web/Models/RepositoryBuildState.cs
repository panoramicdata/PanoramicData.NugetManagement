namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// What the last build of a repository's working tree did, here, in this application.
/// </summary>
/// <remarks>
/// There is no Unknown member: not knowing is the absence of a state, so it is modelled as null —
/// the same way <see cref="RepositoryDashboardRow.IsWorkingTreeClean"/> models an unread tree. A
/// member for it would let "we never built this" be compared, rolled up and coloured as though it
/// were a finding.
/// </remarks>
public enum RepositoryBuildState
{
	/// <summary>The build succeeded.</summary>
	Succeeded,

	/// <summary>The build failed.</summary>
	Failed
}
