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
	/// <param name="isAllowedPath">Whether a repository-relative path may be changed.</param>
	/// <param name="maxPaths">The most non-test files one change may touch.</param>
	public static AiChangePipeline Create(
		IAiChangeGit git,
		IAiChangeTests tests,
		IAiChangeWork work,
		IGitHubWriteApi github,
		Func<string, bool> isAllowedPath,
		int maxPaths)
		=> new(git, tests, work, github, new AiChangeOptions
		{
			Limits = new AiChangeLimits(isAllowedPath, IsTestPath, maxPaths)
		});

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
	/// A unique, branch-safe label.
	/// </summary>
	/// <param name="label">What it is for, for example <c>rule CI-15</c> or <c>issue 42</c>.</param>
	/// <param name="unique">A short unique suffix.</param>
	public static string Slug(string label, string unique)
		=> Unsafe().Replace(label.ToLowerInvariant(), "-").Trim('-') + "-" + unique;
}
