namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// What committing a clone's changes to a governance branch and pushing that branch produced.
/// </summary>
public sealed record BranchDelivery
{
	/// <summary>Whether the git steps worked, including when there turned out to be nothing to deliver.</summary>
	public required bool Success { get; init; }

	/// <summary>The output of the step that failed, when one did.</summary>
	public string Output { get; init; } = string.Empty;

	/// <summary>Whether the clone had nothing that origin's main lacks, so no branch was pushed.</summary>
	public bool NothingToDeliver { get; init; }

	/// <summary>The branch pushed to origin, such as <c>gov/20261128-120000-a1b2c3</c>.</summary>
	public string? Branch { get; init; }

	/// <summary>The branch's head commit.</summary>
	public string? HeadSha { get; init; }

	/// <summary>The head commit's subject, for the pull request title.</summary>
	public string Subject { get; init; } = string.Empty;

	/// <summary>The head commit's full message, for the pull request body.</summary>
	public string Message { get; init; } = string.Empty;

	/// <summary>A git step failed.</summary>
	/// <param name="output">What it printed.</param>
	public static BranchDelivery Failed(string output) => new() { Success = false, Output = output };

	/// <summary>There was nothing to deliver, so nothing was pushed.</summary>
	public static BranchDelivery Nothing { get; } = new() { Success = true, NothingToDeliver = true };
}
