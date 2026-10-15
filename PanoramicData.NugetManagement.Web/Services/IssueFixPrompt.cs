using System.Globalization;
using System.Text;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Builds what the writing model is told about a human-raised issue, which is never the issue.
/// </summary>
/// <remarks>
/// Assembled from the brief's fields and from nothing else. The brief is a paraphrase written by a
/// model that held no tools, so by the time anything reaches a model that can write, every sentence in
/// front of it was produced inside this application — the reporter's own words stopped at the
/// quarantine.
/// <para>
/// Deliberately the same shape as a rule-driven task, because the system prompt and the loop are the
/// same and a familiar structure is worth more to a small model than a bespoke one.
/// </para>
/// </remarks>
public static class IssueFixPrompt
{
	/// <summary>
	/// The task message for one issue fix.
	/// </summary>
	/// <param name="brief">The laundered instruction.</param>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <param name="issueNumber">The issue this came from, for the log and nothing else.</param>
	public static string BuildTask(IssueFixBrief brief, string repositoryFullName, int issueNumber)
	{
		var builder = new StringBuilder();

		builder
			.Append("Repository: ").AppendLine(repositoryFullName)
			.Append("Working from issue #")
			.AppendLine(issueNumber.ToString(CultureInfo.InvariantCulture))
			.AppendLine()
			.Append("Goal: ").AppendLine(brief.Goal)
			.AppendLine()
			.Append("Done looks like: ").AppendLine(brief.ExpectedEndState)
			.AppendLine()
			.AppendLine("You may write only these files:");

		foreach (var path in brief.Paths)
		{
			builder.Append("  ").AppendLine(path);
		}

		builder
			.AppendLine()
			.AppendLine("You may read anything else in the repository for context.");

		if (brief.RelatedRuleId is { Length: > 0 } ruleId)
		{
			builder
				.AppendLine()
				.Append("This also overlaps governance rule ").Append(ruleId)
				.AppendLine(", which will be re-checked afterwards.");
		}

		// Said out loud on purpose. Nobody reads this diff before it merges: a test that fails now must
		// pass, the rest of the tests must still pass, and the change goes out as a pull request that
		// merges itself once CI agrees. A model that thinks a person will catch its mistakes writes
		// more ambitiously than one that knows the tests are the only check.
		builder
			.AppendLine()
			.AppendLine(
				"Make the smallest change that achieves the goal. The tests are the only check: your "
				+ "change is delivered as a pull request that merges itself once every test passes, so "
				+ "do not try to finish anything beyond what is described above.");

		return builder.ToString();
	}
}
