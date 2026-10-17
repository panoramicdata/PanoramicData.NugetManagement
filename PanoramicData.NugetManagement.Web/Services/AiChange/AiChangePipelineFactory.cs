using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// Builds the pipeline with this application's limits.
/// </summary>
public static partial class AiChangePipelineFactory
{
	[GeneratedRegex("[^a-z0-9]+")]
	private static partial Regex Unsafe();

	/// <summary>Creates a pipeline.</summary>
	/// <param name="git">The clone's git operations.</param>
	/// <param name="tests">Building and testing the clone.</param>
	/// <param name="work">The model-facing work; unused when only delivering finished edits.</param>
	/// <param name="github">The GitHub write API.</param>
	/// <param name="limits">What the change may touch.</param>
	/// <param name="changesReady">Called when the edits exist and before the clone is reset, to keep a copy of them.</param>
	public static AiChangePipeline Create(
		IAiChangeGit git,
		IAiChangeTests tests,
		IAiChangeWork work,
		IGitHubWriteApi github,
		AiChangeLimits limits,
		Func<CancellationToken, Task>? changesReady = null)
		=> new(git, tests, work, github, new AiChangeOptions { Limits = limits, ChangesReady = changesReady });

	/// <summary>
	/// The limits for a rule fix: any path, no cap.
	/// </summary>
	/// <remarks>
	/// A rule's edits are bounded by its playbook and by the toolbox, which cannot write outside the clone,
	/// and its prompt comes from an assessment rather than from a stranger's text.
	/// </remarks>
	public static AiChangeLimits RuleFixLimits() => new(_ => true, IsTestPath, int.MaxValue);

	/// <summary>
	/// The limits for an issue fix: the brief's files, anything in the test folders, and a small cap.
	/// </summary>
	/// <param name="briefPaths">The files the brief named, however they were spelled.</param>
	/// <param name="testFolders">The test project folders the red-test step was confined to.</param>
	/// <remarks>
	/// Built from the same two lists the toolboxes were, and compared after the same normalisation, so the
	/// gate accepts exactly what the toolboxes allowed the model to write and no more. A test file is any
	/// file under a test folder, not only one whose name looks like a test: the red-test step may add a
	/// fixture or a runner config there, and the cap counts the fix, not its proof.
	/// </remarks>
	public static AiChangeLimits IssueFixLimits(IReadOnlyCollection<string> briefPaths, IReadOnlyCollection<string> testFolders)
	{
		var allowed = briefPaths.Select(NormalisePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var folders = testFolders.Select(folder => NormalisePath(folder).TrimEnd('/') + "/").ToList();

		bool InTestFolder(string path)
			=> folders.Any(folder => NormalisePath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase));

		return new AiChangeLimits(
			path => allowed.Contains(NormalisePath(path)) || InTestFolder(path) || IsTestPath(path),
			path => InTestFolder(path) || IsTestPath(path),
			IssueAutonomyGate.MaxUnattendedPaths);
	}

	/// <summary>
	/// One spelling for a repository-relative path: forward slashes, no leading <c>./</c> or <c>/</c>.
	/// </summary>
	/// <param name="path">The path as written.</param>
	public static string NormalisePath(string path)
	{
		var normalised = path.Replace('\\', '/');

		while (normalised.StartsWith("./", StringComparison.Ordinal))
		{
			normalised = normalised[2..];
		}

		return normalised.TrimStart('/');
	}

	/// <summary>
	/// Whether a repository-relative path is a test file.
	/// </summary>
	/// <param name="path">The path.</param>
	public static bool IsTestPath(string path)
	{
		var segments = path.Replace('\\', '/').Split('/');

		return segments.Any(segment => segment.EndsWith(".Test", StringComparison.Ordinal) || segment.EndsWith(".Tests", StringComparison.Ordinal))
			|| segments[^1].EndsWith("Tests.cs", StringComparison.Ordinal);
	}

	/// <summary>
	/// Puts a text on one line and cuts it to a length, for a pull request title.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <param name="length">The most characters to return, counting the ellipsis.</param>
	public static string Shorten(string text, int length)
	{
		var oneLine = text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ').Trim();

		return oneLine.Length <= length ? oneLine : oneLine[..(length - 3)] + "...";
	}

	/// <summary>
	/// A unique, branch-safe label.
	/// </summary>
	/// <param name="label">What it is for, for example <c>rule CI-15</c> or <c>issue 42</c>.</param>
	/// <param name="unique">A short unique suffix.</param>
	public static string Slug(string label, string unique)
		=> Unsafe().Replace(label.ToLowerInvariant(), "-").Trim('-') + "-" + unique;
}
