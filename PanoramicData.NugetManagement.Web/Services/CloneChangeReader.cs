using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>What happened to a file in the clone relative to git HEAD.</summary>
public enum ChangeKind
{
	/// <summary>The file exists in HEAD and differs, or was renamed.</summary>
	Modified,

	/// <summary>The file is new: untracked or newly added.</summary>
	Added,

	/// <summary>The file exists in HEAD but not in the clone.</summary>
	Deleted
}

/// <summary>A file changed in the clone.</summary>
/// <param name="Path">The repo-relative path, with forward slashes.</param>
/// <param name="Kind">What happened to the file.</param>
/// <param name="OriginalPath">The path in HEAD, set for a rename.</param>
public sealed record ChangedFile(string Path, ChangeKind Kind, string? OriginalPath = null);

/// <summary>The two sides of one file's diff, or why there is none.</summary>
/// <param name="Original">The text in HEAD.</param>
/// <param name="Modified">The text on disk.</param>
/// <param name="NotDiffableReason">Why the file cannot be diffed, or null when it can.</param>
public sealed record FilePair(string Original, string Modified, string? NotDiffableReason);

/// <summary>Reads what an AI fix session has changed in a clone: git HEAD against the working tree.</summary>
public sealed class CloneChangeReader
{
	private const int MaxBytes = 1_048_576;

	/// <summary>Lists the files that differ from HEAD, or nothing when the clone cannot be read.</summary>
	/// <param name="clonePath">The clone's directory.</param>
	public IReadOnlyList<ChangedFile> ReadChanges(string clonePath)
	{
		if (!Directory.Exists(clonePath))
		{
			return [];
		}

		try
		{
			var output = RunGit(clonePath, "status", "--porcelain=v1", "-z", "--untracked-files=all");
			return output is null ? [] : Parse(output);
		}
		catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
		{
			return [];
		}
	}

	/// <summary>Reads both sides of <paramref name="file"/>.</summary>
	/// <param name="clonePath">The clone's directory.</param>
	/// <param name="file">The file, as returned by <see cref="ReadChanges"/>.</param>
	public FilePair ReadPair(string clonePath, ChangedFile file)
	{
		var diskPath = System.IO.Path.Combine(clonePath, file.Path);
		byte[] modified = [];
		if (file.Kind != ChangeKind.Deleted && File.Exists(diskPath))
		{
			modified = File.ReadAllBytes(diskPath);
		}

		var original = file.Kind == ChangeKind.Added
			? []
			: RunGitBytes(clonePath, "show", $"HEAD:{file.OriginalPath ?? file.Path}") ?? [];

		foreach (var side in new[] { modified, original })
		{
			if (side.Length > MaxBytes)
			{
				return new FilePair(string.Empty, string.Empty, $"file too large ({side.Length} bytes)");
			}

			if (Array.IndexOf(side, (byte)0) >= 0)
			{
				return new FilePair(string.Empty, string.Empty, $"binary file ({side.Length} bytes)");
			}
		}

		return new FilePair(Encoding.UTF8.GetString(original), Encoding.UTF8.GetString(modified), null);
	}

	private static List<ChangedFile> Parse(string porcelainZ)
	{
		var tokens = porcelainZ.Split('\0', StringSplitOptions.RemoveEmptyEntries);
		var changes = new List<ChangedFile>();
		for (var i = 0; i < tokens.Length; i++)
		{
			var token = tokens[i];
			if (token.Length < 4)
			{
				continue;
			}

			var status = token[..2];
			var path = token[3..];
			if (status.Contains('R', StringComparison.Ordinal) || status.Contains('C', StringComparison.Ordinal))
			{
				var oldPath = i + 1 < tokens.Length ? tokens[++i] : null;
				changes.Add(new ChangedFile(path, ChangeKind.Modified, oldPath));
				continue;
			}

			var kind = status == "??" || status.Contains('A', StringComparison.Ordinal) ? ChangeKind.Added
				: status.Contains('D', StringComparison.Ordinal) ? ChangeKind.Deleted
				: ChangeKind.Modified;
			changes.Add(new ChangedFile(path, kind));
		}

		return changes;
	}

	private static string? RunGit(string directory, params string[] args)
	{
		var bytes = RunGitBytes(directory, args);
		return bytes is null ? null : Encoding.UTF8.GetString(bytes);
	}

	private static byte[]? RunGitBytes(string directory, params string[] args)
	{
		var info = new ProcessStartInfo("git")
		{
			WorkingDirectory = directory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		info.ArgumentList.Add("-c");
		info.ArgumentList.Add("core.quotepath=off");
		foreach (var arg in args)
		{
			info.ArgumentList.Add(arg);
		}

		using var process = Process.Start(info) ?? throw new InvalidOperationException("git did not start.");
		using var stdout = new MemoryStream();
		var errors = process.StandardError.ReadToEndAsync();
		process.StandardOutput.BaseStream.CopyTo(stdout);
		process.WaitForExit();
		_ = errors.GetAwaiter().GetResult();
		return process.ExitCode == 0 ? stdout.ToArray() : null;
	}
}
