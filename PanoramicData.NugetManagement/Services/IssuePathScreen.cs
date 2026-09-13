namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Whether the files a fix brief names may be written to, and if not, why not.
/// </summary>
/// <param name="Accepted">Whether every path passed.</param>
/// <param name="Refusal">
/// The first path's reason for failing, in a sentence a human reads on the issue's panel. Empty when
/// accepted. One reason rather than all of them: the first refusal already means a human is looking,
/// and listing the rest invites treating the screen as a checklist to satisfy.
/// </param>
public sealed record IssuePathScreenResult(bool Accepted, string Refusal);

/// <summary>
/// The deterministic check between an untrusted issue and a model that can write to a clone.
/// </summary>
/// <remarks>
/// Every path in a brief is screened here before any session is queued, and the same list becomes the
/// session's write allowlist. The model classifies; this gates — nothing the model says about its own
/// intentions can widen what it is allowed to touch.
/// <para>
/// Paths are refused rather than sanitised, for the reason the AI fix toolbox gives: quietly
/// rewriting an escaping path hides that something tried to escape, and whatever tried once will try
/// again. A refusal is visible, and it is evidence.
/// </para>
/// </remarks>
public static class IssuePathScreen
{
	/// <summary>
	/// The files a fix derived from a stranger's issue may never touch, whatever it says it needs.
	/// </summary>
	/// <remarks>
	/// Build, restore, CI and publish configuration: the surfaces where a change is worth making on
	/// purpose and hardest to spot in a diff. A genuine issue needing one of these is not refused
	/// outright — it becomes a verdict a human reads and queues by hand.
	/// </remarks>
	public static IReadOnlyList<string> ProtectedPatterns { get; } =
	[
		".github/", ".config/", "*.yml", "*.yaml", "*.props", "*.targets",
		"*.sln", "nuget.config", "directory.build.*", "global.json"
	];

	/// <summary>
	/// Screens every path a brief names against one clone.
	/// </summary>
	/// <param name="cloneRoot">The repository's local root.</param>
	/// <param name="paths">The repo-relative paths from the brief.</param>
	/// <param name="alsoProtected">
	/// Extra repo-relative paths to refuse, so a caller can add the files a deterministic remediation
	/// already owns: two writers for one file means the rule and the issue fighting over it.
	/// </param>
	public static IssuePathScreenResult Screen(
		string cloneRoot,
		IReadOnlyList<string> paths,
		IReadOnlyCollection<string>? alsoProtected = null)
	{
		if (paths.Count == 0)
		{
			return new IssuePathScreenResult(
				false,
				"The fix names no files, so there is nothing to restrict its writes to.");
		}

		var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cloneRoot));

		foreach (var path in paths)
		{
			var refusal = ScreenOne(root, path, alsoProtected);

			if (refusal is not null)
			{
				return new IssuePathScreenResult(false, refusal);
			}
		}

		return new IssuePathScreenResult(true, string.Empty);
	}

	private static string? ScreenOne(
		string root,
		string path,
		IReadOnlyCollection<string>? alsoProtected)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return "The fix names an empty path.";
		}

		if (Path.IsPathRooted(path))
		{
			return $"'{path}' is an absolute path; a fix brief names files relative to the repository.";
		}

		string full;

		try
		{
			full = Path.GetFullPath(Path.Combine(root, path));
		}
		catch (ArgumentException)
		{
			return $"'{path}' is not a usable path.";
		}

		if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
		{
			return $"'{path}' resolves outside the repository.";
		}

		if (!File.Exists(full))
		{
			return $"'{path}' does not exist in this repository.";
		}

		var normalised = Path.GetRelativePath(root, full).Replace('\\', '/');

		if (IsProtected(normalised))
		{
			return $"'{path}' is build, CI or publish configuration, which an issue-driven fix "
				+ "may not write to.";
		}

		return alsoProtected?.Any(owned =>
			string.Equals(owned.Replace('\\', '/'), normalised, StringComparison.OrdinalIgnoreCase)) == true
				? $"'{path}' is already maintained by an automatic remediation."
				: null;
	}

	private static bool IsProtected(string normalisedRelativePath)
	{
		var lower = normalisedRelativePath.ToLowerInvariant();
		var fileName = Path.GetFileName(lower);

		foreach (var pattern in ProtectedPatterns)
		{
			var match = pattern.EndsWith('/')
				? lower.StartsWith(pattern, StringComparison.Ordinal)
				: FileNameMatches(fileName, pattern);

			if (match)
			{
				return true;
			}
		}

		return false;
	}

	private static bool FileNameMatches(string fileName, string pattern)
		=> pattern.StartsWith("*.", StringComparison.Ordinal)
			? fileName.EndsWith(pattern[1..], StringComparison.Ordinal)
			: pattern.EndsWith(".*", StringComparison.Ordinal)
				? fileName.StartsWith(pattern[..^1], StringComparison.Ordinal)
				: string.Equals(fileName, pattern, StringComparison.Ordinal);
}
