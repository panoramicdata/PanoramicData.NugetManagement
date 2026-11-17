using System.Diagnostics;
using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

public partial class LocalRepoService
{
	/// <summary>
	/// Gets the URL of the clone's <c>origin</c> remote, or null if it cannot be determined.
	/// </summary>
	/// <remarks>
	/// A directory's name does not prove which repository is checked out in it — it can be renamed, or
	/// re-pointed at a fork. This is the only reliable way to find out, and callers use it to confirm
	/// they are about to write to the repository they think they are.
	/// </remarks>
	public async Task<string?> GetOriginUrlAsync(string repoIdentity, CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path))
		{
			return null;
		}

		var (exitCode, output) = await RunCommandAsync(path, "git", ["remote", "get-url", "origin"], cancellationToken)
			.ConfigureAwait(false);

		return exitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output.Trim() : null;
	}

	/// <summary>
	/// Lists the clones under a root, as the <c>owner/name</c> each one sits at.
	/// </summary>
	public static IReadOnlyList<string> EnumerateClones(string root)
	{
		if (!Directory.Exists(root))
		{
			return [];
		}

		var clones = new List<string>();

		foreach (var ownerDirectory in Directory.EnumerateDirectories(root))
		{
			foreach (var repoDirectory in Directory.EnumerateDirectories(ownerDirectory))
			{
				if (Directory.Exists(Path.Combine(repoDirectory, ".git")))
				{
					clones.Add($"{Path.GetFileName(ownerDirectory)}/{Path.GetFileName(repoDirectory)}");
				}
			}
		}

		return clones;
	}

	/// <summary>
	/// Moves every clone from one root to another, keeping the <c>owner/name</c> layout. Reports what
	/// moved and what did not.
	/// </summary>
	/// <remarks>
	/// Moved rather than left behind, because this root is the app's alone: a clone stranded in the old
	/// place is of no use to anybody and would only be re-downloaded into the new one. Per clone rather
	/// than moving the root wholesale, so an owner directory that already exists at the destination is
	/// merged into rather than colliding with.
	///
	/// A clone that will not move — locked by a build, or already present at the destination — is
	/// reported and skipped rather than aborting the rest, since stopping half way would leave the
	/// clones split across two roots with no way to tell which.
	/// </remarks>
	public (int Moved, IReadOnlyList<string> Failures) MoveClones(
		string fromRoot,
		string toRoot,
		Action<string>? onOutput)
	{
		var moved = 0;
		var failures = new List<string>();

		foreach (var clone in EnumerateClones(fromRoot))
		{
			var source = Path.Combine(fromRoot, clone.Replace('/', Path.DirectorySeparatorChar));
			var destination = Path.Combine(toRoot, clone.Replace('/', Path.DirectorySeparatorChar));

			if (Directory.Exists(destination))
			{
				failures.Add($"{clone} — already present at the destination");
				continue;
			}

			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				MoveDirectory(source, destination);
				onOutput?.Invoke($"📁 moved {clone}");
				moved++;
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Could not move {Clone} from {Source} to {Destination}", clone, source, destination);
				failures.Add($"{clone} — {ex.Message}");
			}
		}

		// Owner directories left empty by the move are just litter in a folder the user may now delete.
		foreach (var ownerDirectory in Directory.Exists(fromRoot) ? Directory.EnumerateDirectories(fromRoot) : [])
		{
			if (!Directory.EnumerateFileSystemEntries(ownerDirectory).Any())
			{
				try
				{
					Directory.Delete(ownerDirectory);
				}
				catch (Exception ex)
				{
					_logger.LogDebug(ex, "Could not remove the emptied {Directory}", ownerDirectory);
				}
			}
		}

		return (moved, failures);
	}

	/// <summary>
	/// Moves a directory, falling back to copy-then-delete when the two are on different volumes.
	/// </summary>
	/// <remarks>
	/// <see cref="Directory.Move(string, string)"/> is a rename, so it fails across volumes — which is
	/// exactly what choosing a clone root on another drive does.
	/// </remarks>
	private static void MoveDirectory(string source, string destination)
	{
		try
		{
			Directory.Move(source, destination);
			return;
		}
		catch (IOException)
		{
			// Falls through to the copy below: most likely a different volume.
		}

		CopyDirectory(source, destination);
		Directory.Delete(source, recursive: true);
	}

	private static void CopyDirectory(string source, string destination)
	{
		Directory.CreateDirectory(destination);

		foreach (var file in Directory.EnumerateFiles(source))
		{
			File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
		}

		foreach (var directory in Directory.EnumerateDirectories(source))
		{
			CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
		}
	}
}
