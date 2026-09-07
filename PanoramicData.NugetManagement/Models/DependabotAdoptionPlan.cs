namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// Exactly what adopting one pull request would write, in the form the existing remediations read.
/// </summary>
/// <param name="PackageUpdates">
/// One pipe-delimited record per package declaration to rewrite:
/// <c>filePath|packageId|versionKind|currentVersion|targetVersion</c>, as
/// <c>update_package_versions</c> parses.
/// </param>
/// <param name="ActionPatterns">Regexes matching the action pins to move.</param>
/// <param name="ActionReplacements">
/// The replacement for each pattern, positionally. Always the same length as
/// <paramref name="ActionPatterns"/>, because <c>replace_regex_in_files</c> pairs them by index.
/// </param>
/// <remarks>
/// Computed by triage rather than by whatever applies it, because everything needed to compute it —
/// what the repository declares and where — is already in hand there, and because a plan is then a
/// pure value a test can assert on without a clone, a network or a working tree.
/// <para>
/// The target version is the one the pull request proposed, never a newer one the version cache
/// happens to know about. Writing further than was asked would pre-empt the grace periods by more
/// than the pull request does, and adoption exists to stop a pull request rotting rather than to
/// overrule PKG-05/06/07. The consequence is accepted: a package adopted at the proposed version may
/// be moved again later by its own rule, and each of those two writes is justified by its own reason.
/// </para>
/// </remarks>
public sealed record DependabotAdoptionPlan(
	IReadOnlyList<string> PackageUpdates,
	IReadOnlyList<string> ActionPatterns,
	IReadOnlyList<string> ActionReplacements)
{
	/// <summary>Whether this plan would write anything at all.</summary>
	public bool HasAnything => PackageUpdates.Count > 0 || ActionPatterns.Count > 0;
}
