namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// One open GitHub issue or pull request, and how long it has gone without a maintainer reply.
/// </summary>
/// <remarks>
/// Issues and pull requests share this type because GitHub's own model does: a pull request is an
/// issue, the list endpoint returns both, and "how long since one of us answered" is the same
/// question for each. <see cref="IsPullRequest"/> separates them where the UI needs to and nowhere
/// else.
/// </remarks>
public class RepositoryIssue
{
	/// <summary>
	/// How long without a maintainer reply before an item is an error.
	/// </summary>
	public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

	/// <summary>
	/// How long without a maintainer reply before an item is critical.
	/// </summary>
	public static readonly TimeSpan CriticalAfter = TimeSpan.FromDays(30);

	/// <summary>The issue or pull request number.</summary>
	public required int Number { get; init; }

	/// <summary>The title, as shown on GitHub.</summary>
	public required string Title { get; init; }

	/// <summary>Whether this item is a pull request rather than an issue.</summary>
	public bool IsPullRequest { get; init; }

	/// <summary>The GitHub web address of the item.</summary>
	public required string HtmlUrl { get; init; }

	/// <summary>The login of whoever opened it. Bots included, and never filtered on.</summary>
	public required string AuthorLogin { get; init; }

	/// <summary>When the item was opened.</summary>
	public required DateTimeOffset CreatedAtUtc { get; init; }

	/// <summary>
	/// When a maintainer — someone whose author association on the comment was Owner, Member or
	/// Collaborator — last commented, or null if none ever has.
	/// </summary>
	public DateTimeOffset? LastMaintainerReplyUtc { get; init; }

	/// <summary>
	/// The instant the staleness clock starts: the last maintainer reply, or the moment the item was
	/// opened when there has never been one. An item nobody has answered has been waiting since it
	/// was raised.
	/// </summary>
	public DateTimeOffset ClockStartUtc => LastMaintainerReplyUtc ?? CreatedAtUtc;

	/// <summary>
	/// How bad this item is at the given instant.
	/// </summary>
	/// <param name="nowUtc">The instant to judge against.</param>
	/// <remarks>
	/// Derived rather than stored, and takes the instant as a parameter rather than reading the
	/// clock. A cached item then reports today's severity when it is read back tomorrow, instead of
	/// a verdict frozen when the network last answered — and the bands are testable without a clock
	/// abstraction. There is deliberately no Warning band: two escalations were asked for, and a
	/// third step in the middle would mean nothing.
	/// </remarks>
	public AssessmentSeverity SeverityAt(DateTimeOffset nowUtc)
	{
		var age = nowUtc - ClockStartUtc;

		if (age >= CriticalAfter)
		{
			return AssessmentSeverity.Critical;
		}

		return age >= StaleAfter
			? AssessmentSeverity.Error
			: AssessmentSeverity.Info;
	}
}
