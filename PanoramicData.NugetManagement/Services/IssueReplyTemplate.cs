using System.Text;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// A public reply, or the reason there will not be one.
/// </summary>
/// <param name="Accepted">Whether the slots fitted the template.</param>
/// <param name="Reply">The assembled comment, or null when they did not.</param>
/// <param name="Refusal">Why they did not, for the issue's panel.</param>
public sealed record IssueReplyResult(bool Accepted, string? Reply, string Refusal);

/// <summary>
/// Builds the only thing this application ever says in public about somebody else's issue.
/// </summary>
/// <remarks>
/// A reply is assembled from fixed sentences with three short slots, not written by the model. The
/// model has just finished reading prose that anyone could have crafted, and a reply is the one output
/// a stranger can read — so anything it contributes is capped in length and screened for the shapes
/// repository contents would take on the way out: links, code, and file paths.
/// <para>
/// A rejection never accuses. It says what could not be verified and invites a correction, because
/// this is the verdict most likely to be wrong about a real person and the cost of being wrong is
/// paid in public under the organisation's name.
/// </para>
/// </remarks>
public static class IssueReplyTemplate
{
	/// <summary>The most a classification slot may run to.</summary>
	public const int MaxClassificationLength = 80;

	/// <summary>The most the one-line reason may run to.</summary>
	public const int MaxReasonLength = 300;

	/// <summary>The most the "what would help" slot may run to.</summary>
	public const int MaxWhatWouldHelpLength = 300;

	/// <summary>
	/// Assembles a reply, or refuses to.
	/// </summary>
	/// <param name="action">The verdict being communicated. Only Answer and Reject have replies.</param>
	/// <param name="classification">A few words naming what this is.</param>
	/// <param name="reason">One line saying why.</param>
	/// <param name="whatWouldHelp">What the reporter could add, when anything would. Optional.</param>
	public static IssueReplyResult Build(
		IssueAction action,
		string classification,
		string reason,
		string? whatWouldHelp)
	{
		if (action is not (IssueAction.Answer or IssueAction.Reject))
		{
			return new IssueReplyResult(
				false, null, $"{action} does not post a reply.");
		}

		foreach (var (slot, value, cap) in new[]
		{
			("classification", classification, MaxClassificationLength),
			("reason", reason, MaxReasonLength),
			("what would help", whatWouldHelp ?? string.Empty, MaxWhatWouldHelpLength)
		})
		{
			var refusal = Screen(slot, value, cap);

			if (refusal is not null)
			{
				return new IssueReplyResult(false, null, refusal);
			}
		}

		return new IssueReplyResult(true, Assemble(action, classification, reason, whatWouldHelp), string.Empty);
	}

	private static string? Screen(string slot, string value, int cap)
	{
		if (value.Length > cap)
		{
			return $"The {slot} ran to {value.Length} characters; the template allows {cap}.";
		}

		if (value.Contains("```", StringComparison.Ordinal) || value.Contains('`'))
		{
			return $"The {slot} contains code, which a generated reply never carries.";
		}

		if (value.Contains("http://", StringComparison.OrdinalIgnoreCase)
			|| value.Contains("https://", StringComparison.OrdinalIgnoreCase)
			|| value.Contains("www.", StringComparison.OrdinalIgnoreCase))
		{
			return $"The {slot} contains a link, which a generated reply never carries.";
		}

		return LooksLikeAPath(value)
			? $"The {slot} names a file, which a generated reply never carries."
			: null;
	}

	/// <summary>
	/// Whether a slot names a file.
	/// </summary>
	/// <remarks>
	/// A path is a separator with a plausible file extension somewhere after it, or a backslash
	/// anywhere. Deliberately broad: a false refusal costs a human glance at a drafted reply, while a
	/// miss posts a repository's internal structure to a stranger.
	/// </remarks>
	private static bool LooksLikeAPath(string value)
	{
		if (value.Contains('\\'))
		{
			return true;
		}

		foreach (var token in value.Split([' ', '\t', '\n', '\r', ',', ';', '(', ')'],
			StringSplitOptions.RemoveEmptyEntries))
		{
			if (!token.Contains('/'))
			{
				continue;
			}

			var extension = Path.GetExtension(token.TrimEnd('.'));

			if (extension.Length is > 1 and <= 6 && extension[1..].All(char.IsLetterOrDigit))
			{
				return true;
			}
		}

		return false;
	}

	private static string Assemble(
		IssueAction action,
		string classification,
		string reason,
		string? whatWouldHelp)
	{
		var builder = new StringBuilder();

		if (action is IssueAction.Answer)
		{
			builder.Append("Thanks for raising this. Having looked at it, we think this is ")
				.Append(classification)
				.Append(": ")
				.Append(reason)
				.Append('.');

			if (!string.IsNullOrWhiteSpace(whatWouldHelp))
			{
				builder.Append(" If that does not match what you are seeing, could you reply with ")
					.Append(whatWouldHelp)
					.Append('?');
			}
		}
		else
		{
			builder.Append("Thanks for raising this, and sorry for the slow reply. We have not been ")
				.Append("able to take this forward: it ")
				.Append(classification)
				.Append(" — ")
				.Append(reason)
				.Append(". We are closing it for now, which is a statement about what we could ")
				.Append("verify and not about the report.");

			if (!string.IsNullOrWhiteSpace(whatWouldHelp))
			{
				builder.Append(" If you can reply with ")
					.Append(whatWouldHelp)
					.Append(", we will happily reopen it.");
			}
			else
			{
				builder.Append(" If we have misread it, please reply and we will reopen it.");
			}
		}

		builder.AppendLine().AppendLine()
			.Append("<sub>Drafted by repository automation and reviewed before posting.</sub>");

		return builder.ToString();
	}
}
