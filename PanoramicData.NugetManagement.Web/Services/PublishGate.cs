using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Decides whether the toolbar offers Publish for the selected repository.
/// </summary>
/// <remarks>
/// Publishing is the one step in the workflow that cannot be taken back: a version pushed to NuGet
/// stays pushed. So the gate is deliberately narrow — the repository has to be cloned, level with
/// origin, and carrying a status that says the code in the clone was actually exercised.
/// <para>
/// The estate-wide "allow publish without running tests" setting waives exactly one of those
/// requirements: that the exercising was a test run rather than a build. It does not waive the
/// others, and in particular it does not turn a failure into a pass — a repository whose build or
/// tests just failed is not publishable however the setting is set.
/// </para>
/// </remarks>
public static class PublishGate
{
	/// <summary>
	/// Whether Publish should be offered for <paramref name="row"/>.
	/// </summary>
	/// <param name="row">The selected repository, or null when none is selected.</param>
	/// <param name="allowWithoutTests">
	/// The "allow publish without running tests" setting. When true, a successful build is accepted
	/// in place of a passing test run.
	/// </param>
	public static bool IsEnabled(RepositoryDashboardRow? row, bool allowWithoutTests)
		=> row is not null
			&& row.IsClonedLocally
			// Only a positive answer blocks: an unknown or expired sync belief is not evidence that
			// the clone is behind, and every other step in the toolbar reads it the same way.
			&& row.IsSyncedWithOrigin != false
			&& (row.Status == PackageStatus.TestsPassed
				|| (allowWithoutTests && row.Status == PackageStatus.BuildSucceeded));

	/// <summary>
	/// Whether Publish is being offered on the strength of the setting rather than a passing test run.
	/// </summary>
	/// <remarks>
	/// The toolbar needs this to say so. A green Publish button normally means the tests passed, and
	/// letting it mean "the tests never ran" without a word of explanation is how a waiver quietly
	/// becomes the default.
	/// </remarks>
	public static bool WaivesTests(RepositoryDashboardRow? row, bool allowWithoutTests)
		=> allowWithoutTests
			&& row is not null
			&& row.Status == PackageStatus.BuildSucceeded;
}
