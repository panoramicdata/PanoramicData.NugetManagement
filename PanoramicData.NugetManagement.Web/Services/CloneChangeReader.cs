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
	private const string CouldNotRead = "could not read file";

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
			if (RunGitBytes(clonePath, "rev-parse", "--verify", "-q", "HEAD") is null)
			{
				return [];
			}

			var output = RunGit(clonePath, "status", "--porcelain=v1", "-z", "--untracked-files=all");
			return output is null ? [] : Parse(output);
		}
		catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
		{
			return [];
		}
	}

	/// <summary>
	/// A fingerprint of <paramref name="file"/>'s on-disk state: lowercase hex SHA-256 of its bytes,
	/// "deleted" when it is absent, "size:{length}:{lastWriteUtcTicks}" over the size cap, or
	/// "unreadable". Never throws.
	/// </summary>
	/// <param name="clonePath">The clone's directory.</param>
	/// <param name="file">The change to fingerprint.</param>
	public string Fingerprint(string clonePath, ChangedFile file)
	{
		try
		{
			var diskPath = System.IO.Path.Combine(clonePath, file.Path);
			if (file.Kind == ChangeKind.Deleted || !File.Exists(diskPath))
			{
				return "deleted";
			}

			var info = new FileInfo(diskPath);
			if (info.Length > MaxBytes)
			{
				return $"size:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
			}

			using var stream = new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			return "unreadable";
		}
	}

	/// <summary>
	/// The files already changed in the clone, each with its <see cref="Fingerprint"/>, keyed by
	/// repository-relative path (ordinal). Empty when the clone cannot be read.
	/// </summary>
	/// <param name="clonePath">The clone's directory.</param>
	public IReadOnlyDictionary<string, string> Baseline(string clonePath)
	{
		var baseline = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var change in ReadChanges(clonePath))
		{
			baseline[change.Path] = Fingerprint(clonePath, change);
		}

		return baseline;
	}

	/// <summary>Reads both sides of <paramref name="file"/>.</summary>
	/// <param name="clonePath">The clone's directory.</param>
	/// <param name="file">The file, as returned by <see cref="ReadChanges"/>.</param>
	public FilePair ReadPair(string clonePath, ChangedFile file)
	{
		try
		{
			byte[] modified = [];
			var diskPath = System.IO.Path.Combine(clonePath, file.Path);
			if (file.Kind != ChangeKind.Deleted && File.Exists(diskPath))
			{
				var length = new FileInfo(diskPath).Length;
				if (length > MaxBytes)
				{
					return NotDiffable($"file too large ({length} bytes)");
				}

				modified = File.ReadAllBytes(diskPath);
			}

			byte[] original = [];
			if (file.Kind != ChangeKind.Added)
			{
				var spec = $"HEAD:{file.OriginalPath ?? file.Path}";
				var sizeBytes = RunGitBytes(clonePath, "cat-file", "-s", spec);
				if (sizeBytes is null || !long.TryParse(Encoding.ASCII.GetString(sizeBytes).Trim(), out var size))
				{
					return NotDiffable(CouldNotRead);
				}

				if (size > MaxBytes)
				{
					return NotDiffable($"file too large ({size} bytes)");
				}

				// --filters gives the working-tree form (eol/smudge applied), so it lines up with the disk side.
				var filtered = RunGitBytes(clonePath, "cat-file", "--filters", spec);
				if (filtered is null)
				{
					return NotDiffable(CouldNotRead);
				}

				original = filtered;
			}

			foreach (var side in new[] { modified, original })
			{
				if (side.Length > MaxBytes)
				{
					return NotDiffable($"file too large ({side.Length} bytes)");
				}

				if (Array.IndexOf(side, (byte)0) >= 0)
				{
					return NotDiffable($"binary file ({side.Length} bytes)");
				}
			}

			return new FilePair(Encoding.UTF8.GetString(original), Encoding.UTF8.GetString(modified), null);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
		{
			return NotDiffable(CouldNotRead);
		}
	}

	private static FilePair NotDiffable(string reason) => new(string.Empty, string.Empty, reason);

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
