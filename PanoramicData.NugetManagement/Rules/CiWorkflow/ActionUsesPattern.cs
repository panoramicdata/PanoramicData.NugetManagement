using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// How a workflow's <c>uses:</c> line is moved to a newer major version.
/// </summary>
/// <remarks>
/// One definition, shared by CI-12 — which moves actions to the version the estate uses elsewhere —
/// and by Dependabot adoption, which moves them to what a pull request proposes. Two copies of this
/// regex would drift, and the failure mode of a drifted version is a rewrite that silently matches
/// nothing rather than an error.
/// </remarks>
public static class ActionUsesPattern
{
	/// <summary>
	/// The workflow files a rewrite applies to.
	/// </summary>
	public static readonly string[] WorkflowGlobs =
		[".github/workflows/*.yml", ".github/workflows/*.yaml"];

	/// <summary>
	/// A pattern matching every pin of an action <em>below</em> the target major, sub-actions included.
	/// </summary>
	/// <param name="action">The action's repository, as <c>owner/name</c>.</param>
	/// <param name="targetSpec">The target version spec, such as <c>v5</c> or <c>5</c>.</param>
	/// <remarks>
	/// Matches <c>actions/checkout@v3</c> and <c>github/codeql-action/analyze@v2</c> alike, because the
	/// sub-actions carry the repository's version rather than one of their own.
	/// <para>
	/// The majors below the target are listed out rather than matched as <c>v\d+</c>, because a pattern
	/// that matches any version rewrites <em>every</em> version — so a workflow already on v9 would be
	/// dragged back to a target of v7 by a fix aimed at a different workflow on v3. A rule that is
	/// careful to treat being ahead as compliant must not then have a remediation that levels
	/// everything to the average.
	/// </para>
	/// <para>
	/// The trailing lookahead is what stops <c>v1</c> matching the first character of <c>v16</c>: with
	/// the alternation alone, a target of v7 would rewrite v10 and v70 as though they were behind. A
	/// commit SHA is left alone for the same reason it is unreadable as a version — it carries no
	/// <c>v</c> prefix at all.
	/// </para>
	/// </remarks>
	public static string Below(string action, string targetSpec)
	{
		var target = GitHubActionVersion.ParseMajor(targetSpec);
		var below = string.Join('|', Enumerable.Range(0, target));

		return $@"({Regex.Escape(action)}(?:/[A-Za-z0-9_.-]+)*@)v(?:{below})(?:\.\d+)*(?![\d.])";
	}

	/// <summary>
	/// The replacement for <see cref="Below"/>, keeping the matched <c>owner/name[/sub]@</c> prefix.
	/// </summary>
	/// <param name="targetSpec">The target version spec.</param>
	/// <remarks>
	/// Normalized to <c>v{major}</c>. A target of <c>v5</c>, <c>5</c> or <c>5.1.2</c> all write
	/// <c>v5</c>: an action's major tag is what a repository pins, and writing a patch-qualified tag
	/// would pin the repository to a tag that may never move again.
	/// </remarks>
	public static string Replacement(string targetSpec)
		=> $"${{1}}v{GitHubActionVersion.ParseMajor(targetSpec)}";
}
