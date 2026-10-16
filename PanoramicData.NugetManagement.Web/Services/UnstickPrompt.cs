using System.Text;
using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// The task a local model is given when a clone is stuck behind uncommitted changes that do not build or
/// do not pass their tests.
/// </summary>
/// <remarks>
/// The changes were made by an automated fix nobody reviewed, so the model is not asked to finish a piece
/// of work: it is asked to make the tree safe to commit, and told that putting a file back is a fine way
/// to do it.
/// </remarks>
public static class UnstickPrompt
{
	/// <summary>The most changed files named in the task: past that the list stops helping and starts filling the window.</summary>
	public const int MaxFilesListed = 40;

	private static readonly Regex _porcelainLine = new(
		@"^\s*[MADRCU?!]{1,2}\s+(?<path>.+)$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant,
		TimeSpan.FromSeconds(1));

	/// <summary>
	/// The task for one stuck clone.
	/// </summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <param name="changedFiles">The <c>git status --porcelain</c> lines of what is uncommitted.</param>
	/// <param name="problem">What the build or tests said.</param>
	public static string BuildTask(string repositoryFullName, IReadOnlyList<string> changedFiles, string problem)
	{
		var builder = new StringBuilder();

		builder.AppendLine($"Repository: {repositoryFullName}");
		builder.AppendLine("Task: get this repository to a state that can be committed.");
		builder.AppendLine();
		builder.AppendLine(
			"The working tree has uncommitted changes, made by an automated tool and never reviewed. They stop the "
			+ "repository syncing with its remote, and they do not yet build and pass their tests. Your job is to "
			+ "make the build succeed and the tests pass, changing as little as you can.");
		builder.AppendLine();
		builder.AppendLine($"Uncommitted changes ({changedFiles.Count}):");

		foreach (var line in changedFiles.Take(MaxFilesListed))
		{
			builder.AppendLine($"  {line}");
		}

		if (changedFiles.Count > MaxFilesListed)
		{
			builder.AppendLine($"  ...and {changedFiles.Count - MaxFilesListed} more.");
		}

		builder.AppendLine();
		builder.AppendLine("What is wrong:");
		builder.AppendLine(problem);
		builder.AppendLine();
		builder.AppendLine("Done means:");
		builder.AppendLine("- run_build succeeds, and no test that passed before these changes fails now.");
		builder.AppendLine("- You touched only the files listed above.");
		builder.AppendLine();
		builder.AppendLine("How to work:");
		builder.AppendLine("1. Read the build or test error, then read the one file it names.");
		builder.AppendLine("2. Correct the change with replace_in_file, then call run_build.");
		builder.AppendLine(
			"3. If a changed file cannot be made to work, call restore_file on it. Putting a file back to the last "
			+ "commit is an acceptable result; leaving the build broken is not.");
		builder.AppendLine("4. Do not delete, skip or weaken a test.");

		return builder.ToString();
	}

	/// <summary>
	/// The paths a stuck clone's <c>git status --porcelain</c> lines name, so a session can be confined to them.
	/// </summary>
	/// <param name="porcelainLines">The status lines.</param>
	/// <returns>Files, and separately the folders git reported whole because nothing in them was tracked.</returns>
	public static (IReadOnlyList<string> Files, IReadOnlyList<string> Folders) PathsIn(IEnumerable<string> porcelainLines)
	{
		var files = new List<string>();
		var folders = new List<string>();

		foreach (var line in porcelainLines)
		{
			// One or two status characters, then the path. Matched rather than sliced at column three,
			// because the command's output is trimmed and the first line may have lost its leading space.
			// A rename reads "old -> new" and the new name is the one on disk.
			var match = _porcelainLine.Match(line);
			if (!match.Success)
			{
				continue;
			}

			var path = match.Groups["path"].Value.Trim();
			var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
			if (arrow >= 0)
			{
				path = path[(arrow + 4)..];
			}

			path = path.Trim('"').Replace('\\', '/');

			if (path.Length == 0)
			{
				continue;
			}

			if (path.EndsWith('/'))
			{
				folders.Add(path.TrimEnd('/'));
			}
			else
			{
				files.Add(path);
			}
		}

		return (files, folders);
	}
}
