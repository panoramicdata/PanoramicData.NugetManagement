namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// Shared package health indicator for consistent UI colouring.
/// </summary>
public enum PackageHealthStatus
{
	/// <summary>
	/// Assessment not available yet (or reassessment in progress).
	/// </summary>
	Pending,

	/// <summary>
	/// No failures.
	/// </summary>
	Success,

	/// <summary>
	/// One or more critical or error failures.
	/// </summary>
	Error,

	/// <summary>
	/// Warning-only failures.
	/// </summary>
	Warning,

	/// <summary>
	/// Informational failures only.
	/// </summary>
	Info,

	/// <summary>
	/// No assessment is available and none is in progress — e.g. the package has no
	/// repository URL in its NuGet metadata, or the repository could not be reached.
	/// </summary>
	Unknown
}

/// <summary>
/// Summary of rule results for a single category.
/// </summary>
public class CategorySummary
{
	/// <summary>
	/// Number of critical-severity failures.
	/// </summary>
	public int Criticals { get; set; }

	/// <summary>
	/// Number of rules that passed.
	/// </summary>
	public int Passed { get; set; }

	/// <summary>
	/// Number of error-severity failures.
	/// </summary>
	public int Errors { get; set; }

	/// <summary>
	/// Number of warning-severity failures.
	/// </summary>
	public int Warnings { get; set; }

	/// <summary>
	/// Number of info-severity failures.
	/// </summary>
	public int Infos { get; set; }

	/// <summary>
	/// Total failures across all severities.
	/// </summary>
	public int TotalFailures => Criticals + Errors + Warnings + Infos;
}

/// <summary>
/// Status of a package row in the dashboard.
/// </summary>
public enum PackageStatus
{
	/// <summary>
	/// Not yet assessed.
	/// </summary>
	NotAssessed,

	/// <summary>
	/// The declared repository is not ours, so nothing is assessed or acted on.
	/// </summary>
	NotGoverned,

	/// <summary>
	/// Assessment is in progress.
	/// </summary>
	Assessing,

	/// <summary>
	/// Assessment complete — review results.
	/// </summary>
	Assessed,

	/// <summary>
	/// Remediation is in progress.
	/// </summary>
	Remediating,

	/// <summary>
	/// Remediation complete — ready for testing.
	/// </summary>
	Remediated,

	/// <summary>
	/// Tests are running.
	/// </summary>
	Testing,

	/// <summary>
	/// Tests passed — ready to publish.
	/// </summary>
	TestsPassed,

	/// <summary>
	/// Tests failed.
	/// </summary>
	TestsFailed,

	/// <summary>
	/// Building in progress.
	/// </summary>
	Building,

	/// <summary>
	/// Build succeeded.
	/// </summary>
	BuildSucceeded,

	/// <summary>
	/// Build failed.
	/// </summary>
	BuildFailed,

	/// <summary>
	/// Publishing in progress.
	/// </summary>
	Publishing,

	/// <summary>
	/// Published successfully.
	/// </summary>
	Published,

	/// <summary>
	/// Git sync in progress.
	/// </summary>
	GitSyncing,

	/// <summary>
	/// Git sync complete.
	/// </summary>
	GitSynced,

	/// <summary>
	/// An error occurred.
	/// </summary>
	Error,

	/// <summary>
	/// Not cloned locally.
	/// </summary>
	NotCloned,

	/// <summary>
	/// Cloning in progress.
	/// </summary>
	Cloning
}
