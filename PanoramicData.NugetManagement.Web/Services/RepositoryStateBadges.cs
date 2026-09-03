using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// The two badges on the right of a repository node: where its working tree stands, and whether it
/// builds.
/// </summary>
/// <remarks>
/// Both were once folded into the health glyph on the left, which made one icon answer three
/// questions at once. Neither is a rule failure, and a red glyph that could mean "fails a rule" or
/// "does not build" means neither — so they are separate marks, in a separate place, and they take
/// no part in any roll-up.
/// <para>
/// Static and here rather than in the page, so the mapping can be unit tested: the Web project has
/// no bUnit reference, and a badge that renders the wrong colour is exactly the kind of mistake that
/// otherwise reaches a human first.
/// </para>
/// </remarks>
public static class RepositoryStateBadges
{
	/// <summary>
	/// The header chip shown while Codacy is part-way through an analysis, or null when there is
	/// nothing to say.
	/// </summary>
	/// <param name="state">
	/// Where Codacy's analysis had got to, or null when that could not be established.
	/// </param>
	/// <remarks>
	/// Null in every case but an analysis actually running, and deliberately so: forty-odd
	/// repositories are current at any time, and a chip on all of them is noise that trains the reader
	/// to ignore the one that matters. An unknown state is also null — not knowing an analysis is
	/// running is not the same as knowing one is, and a chip would assert the latter.
	/// </remarks>
	public static string? CodacyAnalysisChip(CodacyAnalysisState? state)
	{
		if (state is not { IsAnalysing: true })
		{
			return null;
		}

		// A missing percentage is left out rather than shown as 0%, which reads as an analysis that
		// has stalled.
		return state.ProgressPercent is { } percent
			? $"Codacy analysing {percent}%"
			: "Codacy analysing";
	}

	/// <summary>
	/// The git badge, worst-first: no clone at all, then uncommitted work, then drift from origin,
	/// then an unread sync state, and only then clean.
	/// </summary>
	/// <param name="node">The repository node.</param>
	public static string GitIcon(NavItem node)
	{
		if (!node.IsClonedLocally)
		{
			return "fa-cloud text-muted";
		}

		if (node.IsWorkingTreeDirty)
		{
			return "fa-pen text-warning";
		}

		return node.IsSyncedWithOrigin switch
		{
			false => "fa-arrows-rotate text-warning",
			null => "fa-code-branch text-muted",
			_ => "fa-code-branch text-success"
		};
	}

	/// <summary>What the git badge means, in words.</summary>
	/// <param name="node">The repository node.</param>
	public static string GitTooltip(NavItem node)
	{
		if (!node.IsClonedLocally)
		{
			return "Not cloned locally";
		}

		if (node.IsWorkingTreeDirty)
		{
			return "Uncommitted changes in the working tree";
		}

		return node.IsSyncedWithOrigin switch
		{
			false => "Out of step with origin",
			null => "Clean; not checked against origin",
			_ => "Clean and in step with origin"
		};
	}

	/// <summary>
	/// The build badge. One glyph, three colours: grey where nothing is known, which covers both
	/// never built here and changed since — to a reader they are the same thing.
	/// </summary>
	/// <param name="node">The repository node.</param>
	public static string BuildIcon(NavItem node) => node.BuildState switch
	{
		RepositoryBuildState.Succeeded => "fa-hammer text-success",
		RepositoryBuildState.Failed => "fa-hammer text-danger",
		_ => "fa-hammer text-muted"
	};

	/// <summary>What the build badge means, in words.</summary>
	/// <param name="node">The repository node.</param>
	public static string BuildTooltip(NavItem node) => node.BuildState switch
	{
		RepositoryBuildState.Succeeded => "Built successfully",
		RepositoryBuildState.Failed => "Build failed",
		_ => "Never built here, or changed since it was"
	};
}
