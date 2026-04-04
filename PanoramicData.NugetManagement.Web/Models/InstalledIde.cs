namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// Represents an IDE detected on the local machine.
/// </summary>
public sealed class InstalledIde
{
	/// <summary>
	/// Gets the unique identifier for this IDE (e.g. "vs2022", "vscode").
	/// </summary>
	public required string Id { get; init; }

	/// <summary>
	/// Gets the display name shown in the UI (e.g. "Visual Studio 2022").
	/// </summary>
	public required string DisplayName { get; init; }

	/// <summary>
	/// Gets the full path to the IDE executable.
	/// </summary>
	public required string ExecutablePath { get; init; }

	/// <summary>
	/// Gets the Font Awesome icon CSS class for this IDE.
	/// </summary>
	public required string IconCss { get; init; }

	/// <summary>
	/// Gets whether this IDE opens solution files (true) or folders (false).
	/// </summary>
	public bool OpensSolutionFiles { get; init; }
}
