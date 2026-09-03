namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// One file a failure's fix splits into, described on its own terms.
/// </summary>
/// <param name="Path">The file, relative to the repository root.</param>
/// <param name="Summary">
/// What is wrong with this file, in one sentence. Replaces the rule's repository-wide message for a
/// session working on this file alone.
/// </param>
/// <param name="Detail">
/// What to fix in this file, and nothing about any other. Replaces the advisory's repository-wide
/// <see cref="RuleAdvisory.Detail"/>.
/// </param>
/// <remarks>
/// The path alone is not enough, and assuming it was is what made the first attempt at this useless:
/// the session was told "change this one file and no other" and then handed a rule message naming
/// three files, a goal reading "improve 3 file(s)", and a guidance section listing all three with
/// their issues. A small model reads the whole prompt, and two thirds of that prompt was about files
/// it must not touch.
/// </remarks>
public sealed record AdvisoryTarget(string Path, string Summary, string Detail);

/// <summary>
/// Structured advisory data emitted by a governance rule when it fails.
/// Designed for consumption by AI agents performing automated remediation.
/// </summary>
public sealed class RuleAdvisory
{
	/// <summary>
	/// One-line summary suitable for a bullet point in an AI prompt.
	/// Example: "Add SECURITY.md with the standard security policy content"
	/// </summary>
	public required string Summary { get; init; }

	/// <summary>
	/// Detailed multi-line advisory in markdown, suitable for an AI agent
	/// to understand the full context and perform the remediation.
	/// May include code snippets, file paths, expected content, etc.
	/// </summary>
	public required string Detail { get; init; }

	/// <summary>
	/// Structured key/value data providing machine-readable context.
	/// Keys use snake_case naming (e.g. "missing_files", "outdated_packages").
	/// Values may be strings, string arrays, or dictionaries for complex data.
	/// Consumers can use these to build targeted prompts or filter/group advisories.
	/// </summary>
	public Dictionary<string, object> Data { get; init; } = [];

	/// <summary>
	/// The files this failure's fix splits across, when it splits at all.
	/// </summary>
	/// <remarks>
	/// Read by Fix with AI, which queues one session per target rather than one per rule. A small model
	/// given "improve these three files" plans all three inside its turn budget and finishes none; given
	/// one file it changes one file. Null — the default — means the fix is one piece of work, which is
	/// true of nearly every rule.
	/// <para>
	/// A rule that sets this owns the length of the list. Every entry becomes a queued item and a GPU
	/// session, so a rule with fifty poor files must name the ones worth fixing and say in its
	/// <see cref="Detail"/> that it did.
	/// </para>
	/// </remarks>
	public IReadOnlyList<AdvisoryTarget>? Targets { get; init; }
}
