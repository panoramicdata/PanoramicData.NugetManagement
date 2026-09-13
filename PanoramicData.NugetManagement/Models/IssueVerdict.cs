namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// What should happen about one human-raised issue.
/// </summary>
/// <remarks>
/// Four outcomes rather than valid/invalid, because the common case for a library is none of those:
/// the reporter understood the code correctly and used it wrongly, which deserves an answer and not a
/// change. Splitting that from <see cref="Reject"/> is most of what stops the feature being rude.
/// </remarks>
public enum IssueAction
{
	/// <summary>Leave it for a human. The default whenever anything is unclear.</summary>
	Escalate = 0,

	/// <summary>A real defect or gap with a bounded change behind it.</summary>
	Fix,

	/// <summary>Nothing to change; the reporter needs an explanation.</summary>
	Answer,

	/// <summary>Invalid, obsolete, or an attempt to manipulate whoever reads it.</summary>
	Reject
}

/// <summary>
/// How sure the analysis is of its own verdict.
/// </summary>
public enum IssueConfidence
{
	/// <summary>Not sure. Always a human's decision.</summary>
	Low = 0,

	/// <summary>Fairly sure, but a human should look.</summary>
	Medium,

	/// <summary>Sure enough to be worth acting on unattended, if nothing else objects.</summary>
	High
}

/// <summary>
/// Something about an issue that costs it the right to be acted on unattended.
/// </summary>
/// <remarks>
/// Flags are not graded and are not weighed against each other. Any one of them sends the issue to a
/// human, because grading them would hand the decision back to the model whose judgement the flags
/// exist to double-check.
/// </remarks>
public enum IssueRiskFlag
{
	/// <summary>The text tries to direct whoever — or whatever — is reading it.</summary>
	InstructsTheReader,

	/// <summary>It asks for a package, feed or external component to be added.</summary>
	RequestsNewDependency,

	/// <summary>It asks for a credential, token or secret, or for one to be handled differently.</summary>
	RequestsCredentialOrSecret,

	/// <summary>The change would reach CI, release or publish configuration.</summary>
	TouchesCiOrPublish,

	/// <summary>The body carries links out to somewhere the reader is invited to go.</summary>
	ContainsExternalLinks,

	/// <summary>The author has no accepted history with this project.</summary>
	UnknownAuthor,

	/// <summary>The report cannot be checked from what it says.</summary>
	Unreproducible,

	/// <summary>The code may well have moved past it since it was raised.</summary>
	PossiblyObsolete,

	/// <summary>The change it implies is wider than one report should carry.</summary>
	ScopeTooLarge
}

/// <summary>
/// The laundered instruction that is all a fixing model ever sees of an issue.
/// </summary>
/// <param name="Goal">What to achieve, in one sentence, in the analysis model's own words.</param>
/// <param name="Paths">
/// The repo-relative files the change belongs in. Doubles as the write allowlist for the session, so
/// this is a security boundary and not a hint.
/// </param>
/// <param name="ExpectedEndState">What being finished looks like.</param>
/// <param name="RelatedRuleId">
/// A governance rule the change happens to overlap, when there is one — which gives the fix back the
/// real oracle that issue-driven work otherwise lacks.
/// </param>
/// <remarks>
/// Every field is a paraphrase. There is deliberately no field carrying the reporter's own words
/// through to the model that can write: passing prose through a tool-less analysis and keeping only
/// its summary is the step that makes the rest of the design safe, and a passthrough field would
/// quietly undo it.
/// </remarks>
public sealed record IssueFixBrief(
	string Goal,
	IReadOnlyList<string> Paths,
	string ExpectedEndState,
	string? RelatedRuleId);

/// <summary>
/// What the analysis concluded about one human-raised issue.
/// </summary>
/// <param name="Action">What should happen.</param>
/// <param name="Confidence">How sure it is.</param>
/// <param name="Risks">Everything that disqualifies the issue from unattended action.</param>
/// <param name="Reasoning">Why, in prose, for a human to read. Always populated.</param>
/// <param name="DraftReply">The templated public reply, for Answer and Reject. Null otherwise.</param>
/// <param name="Brief">The laundered instruction, for Fix. Null otherwise.</param>
public sealed record IssueVerdict(
	IssueAction Action,
	IssueConfidence Confidence,
	IReadOnlyList<IssueRiskFlag> Risks,
	string Reasoning,
	string? DraftReply,
	IssueFixBrief? Brief);
