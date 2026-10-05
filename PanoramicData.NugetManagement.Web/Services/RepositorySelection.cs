namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// How much of what is visible is ticked, for a header checkbox that has three faces.
/// </summary>
public enum SelectionState
{
	/// <summary>Nothing visible is ticked, or nothing is visible.</summary>
	None,

	/// <summary>Some, but not all, of what is visible is ticked.</summary>
	Some,

	/// <summary>Everything visible is ticked.</summary>
	All
}

/// <summary>
/// The repositories the toolbar will act on: the ones ticked in the table.
/// </summary>
/// <remarks>
/// Names are compared without regard to case, because GitHub reports a repository's canonical casing
/// and a cached row may carry another; treating them as different repositories would quietly act on
/// fewer than were ticked. A separate type rather than state on the page, for the reason
/// <see cref="ToolbarScope"/> gives: the page cannot be unit tested, and this is the part worth being
/// sure of.
/// </remarks>
public sealed class RepositorySelection
{
	private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>How many repositories are ticked.</summary>
	public int Count => _names.Count;

	/// <summary>The ticked repositories' full names.</summary>
	public IReadOnlyCollection<string> Names => _names;

	/// <summary>Whether a repository is ticked.</summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	public bool Contains(string repositoryFullName) => _names.Contains(repositoryFullName);

	/// <summary>Ticks a repository that is not ticked, and unticks one that is.</summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <returns>Whether it is ticked afterwards.</returns>
	public bool Toggle(string repositoryFullName)
	{
		if (_names.Remove(repositoryFullName))
		{
			return false;
		}

		_names.Add(repositoryFullName);
		return true;
	}

	/// <summary>Ticks a repository.</summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	public void Select(string repositoryFullName) => _names.Add(repositoryFullName);

	/// <summary>Unticks a repository.</summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	public void Deselect(string repositoryFullName) => _names.Remove(repositoryFullName);

	/// <summary>Ticks every named repository, leaving any other ticks as they are.</summary>
	/// <param name="repositoryFullNames">The repositories to tick.</param>
	public void SelectAll(IEnumerable<string> repositoryFullNames)
	{
		foreach (var name in repositoryFullNames)
		{
			_names.Add(name);
		}
	}

	/// <summary>Unticks every named repository, leaving any other ticks as they are.</summary>
	/// <param name="repositoryFullNames">The repositories to untick.</param>
	public void DeselectAll(IEnumerable<string> repositoryFullNames)
	{
		foreach (var name in repositoryFullNames)
		{
			_names.Remove(name);
		}
	}

	/// <summary>Unticks everything.</summary>
	public void Clear() => _names.Clear();

	/// <summary>
	/// Drops ticks for repositories that no longer exist, so a re-assess or a removal cannot leave the
	/// toolbar naming something that is not there.
	/// </summary>
	/// <param name="stillPresent">Every repository that exists now.</param>
	/// <returns>How many ticks were dropped.</returns>
	public int Prune(IEnumerable<string> stillPresent)
	{
		var keep = new HashSet<string>(stillPresent, StringComparer.OrdinalIgnoreCase);
		return _names.RemoveWhere(name => !keep.Contains(name));
	}

	/// <summary>How much of <paramref name="visible"/> is ticked.</summary>
	/// <param name="visible">The repositories currently on screen.</param>
	public SelectionState StateOf(IReadOnlyCollection<string> visible)
	{
		if (visible.Count == 0)
		{
			return SelectionState.None;
		}

		var ticked = visible.Count(Contains);

		return ticked == 0
			? SelectionState.None
			: ticked == visible.Count ? SelectionState.All : SelectionState.Some;
	}
}
