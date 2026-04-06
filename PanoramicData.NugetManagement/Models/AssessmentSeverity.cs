namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// The severity of an assessment rule violation.
/// </summary>
public enum AssessmentSeverity
{
	/// <summary>
	/// Informational finding — does not affect compliance.
	/// </summary>
	Info,

	/// <summary>
	/// Warning — should be addressed but not a blocker.
	/// </summary>
	Warning,

	/// <summary>
	/// Error — must be fixed for compliance.
	/// </summary>
	Error,

	/// <summary>
	/// Critical finding — highest severity and an immediate blocker.
	/// </summary>
	Critical

}
