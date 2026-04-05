namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// Represents a detected IDE installation on the local machine.
/// </summary>
public sealed class InstalledIde
{
	/// <summary>
	/// Gets the unique identifier for this IDE (e.g. "vscode-insiders", "vs2026-professional").
	/// </summary>
	public required string Id { get; init; }

	/// <summary>
	/// Gets the human-readable display name of the IDE.
	/// </summary>
	public required string DisplayName { get; init; }

	/// <summary>
	/// Gets the full path to the IDE executable.
	/// </summary>
	public required string ExecutablePath { get; init; }

	/// <summary>
	/// Gets the CSS class for the IDE icon (Font Awesome).
	/// </summary>
	public required string IconCss { get; init; }

	/// <summary>
	/// Gets a value indicating whether this IDE opens solution files (.sln/.slnx) rather than folders.
	/// </summary>
	public bool OpensSolutionFiles { get; init; }
}
