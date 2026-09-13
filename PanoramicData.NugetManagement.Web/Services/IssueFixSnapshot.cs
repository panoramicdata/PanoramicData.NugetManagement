namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// The contents of the files an issue-driven fix is allowed to write, as they were before it ran.
/// </summary>
/// <remarks>
/// A rule-driven session that gives up may leave a partial change behind: the rule still fails, the
/// next assessment says so, and the mess announces itself. An issue-driven one has nothing pointing at
/// it — a re-assessment comes back clean over a half-finished change derived from a stranger's prose,
/// and the next person to commit sweeps it up without knowing what it is.
/// <para>
/// So a failed attempt is undone. Only the files on the session's allowlist are captured and restored:
/// the clone is shared with whoever else is working in it, and a broader revert would throw away
/// somebody else's work.
/// </para>
/// </remarks>
public sealed class IssueFixSnapshot
{
	private readonly string _root;

	private readonly Dictionary<string, string?> _contents;

	private IssueFixSnapshot(string root, Dictionary<string, string?> contents)
	{
		_root = root;
		_contents = contents;
	}

	/// <summary>
	/// Reads the current state of every path a session may write.
	/// </summary>
	/// <param name="cloneRoot">The repository's local root.</param>
	/// <param name="paths">The repo-relative paths on the session's allowlist.</param>
	/// <remarks>
	/// A path that does not exist yet is captured as null, so restoring deletes whatever the session
	/// created rather than leaving an invented file behind.
	/// </remarks>
	public static IssueFixSnapshot Capture(string cloneRoot, IReadOnlyCollection<string> paths)
	{
		var root = Path.GetFullPath(cloneRoot);
		var contents = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

		foreach (var path in paths)
		{
			var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
			contents[path] = File.Exists(full) ? File.ReadAllText(full) : null;
		}

		return new IssueFixSnapshot(root, contents);
	}

	/// <summary>
	/// Puts every captured file back as it was.
	/// </summary>
	public void Restore()
	{
		foreach (var (path, content) in _contents)
		{
			var full = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));

			if (content is null)
			{
				if (File.Exists(full))
				{
					File.Delete(full);
				}

				continue;
			}

			File.WriteAllText(full, content);
		}
	}
}
