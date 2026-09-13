using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Applies a repository's waivers to the results its rules produced.
/// </summary>
/// <remarks>
/// One implementation for every assessment path. The three loops that needed this each used to
/// decide for themselves whether a rule was suppressed, and had already drifted — one compared rule
/// ids case-insensitively and the others did not, so the same decision took effect on one path and
/// not the other.
/// </remarks>
public static class RuleWaivers
{
	/// <summary>
	/// Returns the result as the repository's waivers leave it: unchanged when no waiver names the
	/// rule, and excused when one does.
	/// </summary>
	/// <param name="result">The verdict the rule gave.</param>
	/// <param name="options">The repository's options, carrying its waivers.</param>
	/// <remarks>
	/// A waived result passes, says why, and keeps the verdict it would otherwise have given. Its
	/// advisory is dropped: an advisory is an offer to fix, and nothing should offer to fix what the
	/// estate has agreed to leave alone.
	/// </remarks>
	public static RuleResult Apply(RuleResult result, RepoOptions options)
	{
		ArgumentNullException.ThrowIfNull(result);
		ArgumentNullException.ThrowIfNull(options);

		var waiver = Find(result.RuleId, options);
		if (waiver is null)
		{
			return result;
		}

		return new RuleResult
		{
			RuleId = result.RuleId,
			RuleName = result.RuleName,
			Category = result.Category,
			Severity = result.Severity,
			IsApplicable = result.IsApplicable,
			Passed = true,
			Message = $"Waived: {waiver.Reason}",
			Waiver = new AppliedWaiver
			{
				Waiver = waiver,
				UnderlyingPassed = result.Passed,
				UnderlyingMessage = result.Message
			}
		};
	}

	/// <summary>
	/// The waiver covering a rule, or null when the repository is held to it.
	/// </summary>
	/// <remarks>
	/// Rule ids are compared case-insensitively. They are typed by hand into a waivers file and
	/// declared in code as constants, and nothing makes the two agree on casing.
	/// </remarks>
	private static RuleWaiver? Find(string ruleId, RepoOptions options)
	{
		var waiver = options.Waivers
			.FirstOrDefault(w => string.Equals(w.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));

		if (waiver is not null)
		{
			return waiver;
		}

		// How a library consumer said this before waivers existed. Still honoured, but it cannot say
		// why, and the result is explicit about that rather than inventing a justification.
#pragma warning disable CS0618 // Honouring the obsolete member is the point.
		var suppressed = options.SuppressedRules
			.FirstOrDefault(id => string.Equals(id, ruleId, StringComparison.OrdinalIgnoreCase));
#pragma warning restore CS0618

		return suppressed is null
			? null
			: new RuleWaiver
			{
				RuleId = suppressed,
				Reason = "Suppressed with no reason given."
			};
	}
}
