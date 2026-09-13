using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Whether one action may be taken without a human seeing the verdict, and why not when it may not.
/// </summary>
/// <param name="Allowed">Whether the application may act unattended.</param>
/// <param name="Reason">
/// What is holding it back, for the issue's panel. Populated even when allowed, so the console can say
/// what cleared rather than only that something did.
/// </param>
public sealed record IssueGateDecision(bool Allowed, string Reason);

/// <summary>
/// The boundary between what a model concluded and what the application will do about it unattended.
/// </summary>
/// <remarks>
/// The whole design rests on one asymmetry: the model classifies, and this gates. A verdict is an
/// opinion formed by reading text that anyone on the internet can write, so it is treated as evidence
/// towards acting and never as permission to act. Every condition here can only subtract.
/// <para>
/// Rejection and escalation are never automatic at any confidence. A wrong fix is a bad diff in a
/// clone, which is private and recoverable; a wrong rejection is a public accusation against a real
/// person, published under the organisation's name, and it is also precisely what somebody would aim
/// for who wanted a genuine report closed unread.
/// </para>
/// </remarks>
public static class IssueAutonomyGate
{
	/// <summary>
	/// The author associations that count as a history of accepted work.
	/// </summary>
	private static readonly HashSet<string> KnownAssociations =
		new(StringComparer.OrdinalIgnoreCase) { "Owner", "Member", "Collaborator", "Contributor" };

	/// <summary>
	/// The most files an unattended issue-driven fix may touch.
	/// </summary>
	/// <remarks>
	/// Breadth is the tell that the model has understood something larger than the report in front of
	/// it, and it is also what makes a wrong fix expensive to unpick. A wider change is not refused,
	/// only handed to a human.
	/// </remarks>
	public const int MaxUnattendedPaths = 3;

	/// <summary>
	/// Whether a fix may be queued against the clone without a human reading the verdict.
	/// </summary>
	/// <param name="verdict">What the analysis concluded.</param>
	/// <param name="authorAssociation">GitHub's association for whoever raised the issue.</param>
	/// <param name="cloneRoot">The repository's local root, for screening the brief's paths.</param>
	/// <param name="remediationOwned">Files a deterministic remediation already maintains.</param>
	public static IssueGateDecision MayFixAutomatically(
		IssueVerdict verdict,
		string authorAssociation,
		string cloneRoot,
		IReadOnlyCollection<string>? remediationOwned = null)
	{
		if (verdict.Action is not IssueAction.Fix)
		{
			return new IssueGateDecision(
				false,
				$"The verdict is {verdict.Action}, not Fix.");
		}

		if (verdict.Brief is null)
		{
			return new IssueGateDecision(
				false,
				"The verdict asks for a fix but describes none, so there is nothing to contain.");
		}

		if (verdict.Confidence is not IssueConfidence.High)
		{
			return new IssueGateDecision(
				false,
				$"Confidence is {verdict.Confidence}; unattended fixes need High.");
		}

		if (verdict.Risks.Count > 0)
		{
			return new IssueGateDecision(
				false,
				$"Flagged: {string.Join(", ", verdict.Risks)}.");
		}

		if (!KnownAssociations.Contains(authorAssociation))
		{
			return new IssueGateDecision(
				false,
				$"The author's association is '{authorAssociation}'; this project has taken no work "
					+ "from them before.");
		}

		if (verdict.Brief.Paths.Count > MaxUnattendedPaths)
		{
			return new IssueGateDecision(
				false,
				$"The fix spans {verdict.Brief.Paths.Count} files; {MaxUnattendedPaths} is the most "
					+ "that runs unattended.");
		}

		var screen = IssuePathScreen.Screen(cloneRoot, verdict.Brief.Paths, remediationOwned);

		return screen.Accepted
			? new IssueGateDecision(
				true,
				$"Confident, unflagged, {verdict.Brief.Paths.Count} screened file(s), author is "
					+ $"{authorAssociation}.")
			: new IssueGateDecision(false, screen.Refusal);
	}

	/// <summary>
	/// Whether a drafted reply may be posted without a human reading it.
	/// </summary>
	/// <param name="verdict">What the analysis concluded.</param>
	/// <remarks>
	/// Answering is the only unattended action with a public voice, which makes it the exfiltration
	/// route worth worrying about: text crafted to steer the draft reaches everyone who can read the
	/// issue. The analysis is starved of file contents for that reason, and the reply is templated for
	/// the same one — this gate is the third of those three guards, not the only one.
	/// </remarks>
	public static IssueGateDecision MayAnswerAutomatically(IssueVerdict verdict)
	{
		if (verdict.Action is not IssueAction.Answer)
		{
			return new IssueGateDecision(false, $"The verdict is {verdict.Action}, not Answer.");
		}

		if (verdict.Confidence is not IssueConfidence.High)
		{
			return new IssueGateDecision(
				false,
				$"Confidence is {verdict.Confidence}; unattended replies need High.");
		}

		return verdict.Risks.Count > 0
			? new IssueGateDecision(false, $"Flagged: {string.Join(", ", verdict.Risks)}.")
			: string.IsNullOrWhiteSpace(verdict.DraftReply)
				? new IssueGateDecision(false, "There is no drafted reply to post.")
				: new IssueGateDecision(true, "Confident, unflagged, and a reply is drafted.");
	}
}
