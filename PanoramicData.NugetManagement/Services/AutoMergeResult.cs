namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// How GitHub answered a request to auto-merge a pull request.
/// </summary>
public enum AutoMergeState
{
	/// <summary>Auto-merge is on: GitHub merges once the required checks pass.</summary>
	Enabled,

	/// <summary>GitHub refused because nothing is left to wait for, so the pull request can be merged now.</summary>
	AlreadyMergeable,

	/// <summary>GitHub refused for any other reason, such as auto-merge being off for the repository.</summary>
	Refused
}

/// <summary>
/// GitHub's answer to a request to auto-merge a pull request.
/// </summary>
/// <param name="State">What GitHub decided.</param>
/// <param name="Message">GitHub's own words when it refused; null when it did not.</param>
public sealed record AutoMergeResult(AutoMergeState State, string? Message);
