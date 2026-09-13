using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Which open items a person actually raised.
/// </summary>
/// <remarks>
/// Two rules, and deliberately not a list of known bots. A cleverer filter fails by silently never
/// analysing somebody's real report, and that failure is indistinguishable from the feature working
/// — whereas analysing one bot's issue by mistake costs one wasted model call that a human then reads.
/// </remarks>
public static class HumanIssueFilter
{
	/// <summary>GitHub's own suffix on machine accounts.</summary>
	private const string BotSuffix = "[bot]";

	/// <summary>
	/// Whether this item is an issue raised by a person.
	/// </summary>
	/// <param name="issue">The open item.</param>
	public static bool IsHumanRaised(RepositoryIssue issue)
		=> !issue.IsPullRequest
			&& !issue.AuthorLogin.EndsWith(BotSuffix, StringComparison.OrdinalIgnoreCase);
}
