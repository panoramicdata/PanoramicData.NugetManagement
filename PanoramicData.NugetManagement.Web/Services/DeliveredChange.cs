namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>One file of a delivered change, with both sides of its diff.</summary>
/// <param name="File">The file and what happened to it.</param>
/// <param name="Pair">The text before and after, or why there is none.</param>
public sealed record DeliveredFile(ChangedFile File, FilePair Pair);

/// <summary>
/// The change set an AI fix delivered, kept on its work item.
/// </summary>
/// <remarks>
/// The Changes tab reads the clone, and the AI change pipeline returns the clone to <c>main</c> once it has
/// opened or merged the pull request, so the clone holds nothing to read afterwards. This is what was in it
/// just before, so the tab can still show what was delivered. It lives only as long as the item does.
/// </remarks>
/// <param name="Files">The changed files, each with its diff.</param>
/// <param name="Summary">How delivery ended, for a line above the diff.</param>
/// <param name="PullRequestUrl">The pull request, when one was opened.</param>
public sealed record DeliveredChange(IReadOnlyList<DeliveredFile> Files, string Summary, string? PullRequestUrl)
{
	/// <summary>The most text kept across all files, so a finished item cannot hold a clone's worth of memory.</summary>
	public static int DefaultMaxCharacters { get; } = 4_000_000;

	/// <summary>
	/// Reads what is changed in the clone now, both sides of each file, keeping up to <see cref="DefaultMaxCharacters"/>.
	/// </summary>
	/// <param name="reader">Reads the clone.</param>
	/// <param name="clonePath">The clone's directory.</param>
	public static DeliveredChange Capture(CloneChangeReader reader, string clonePath)
		=> Capture(reader, clonePath, DefaultMaxCharacters);

	/// <summary>
	/// Reads what is changed in the clone now, both sides of each file.
	/// </summary>
	/// <param name="reader">Reads the clone.</param>
	/// <param name="clonePath">The clone's directory.</param>
	/// <param name="maxCharacters">The most text to keep; a file that would exceed it is listed but not kept.</param>
	public static DeliveredChange Capture(CloneChangeReader reader, string clonePath, int maxCharacters)
	{
		var files = new List<DeliveredFile>();
		var kept = 0;

		foreach (var change in ChangesTabModel.Listed(reader.ReadChanges(clonePath)))
		{
			var pair = reader.ReadPair(clonePath, change);

			if (pair.NotDiffableReason is null)
			{
				var size = pair.Original.Length + pair.Modified.Length;

				if (kept + size > maxCharacters)
				{
					pair = new FilePair(string.Empty, string.Empty, "not kept: too much changed text to hold");
				}
				else
				{
					kept += size;
				}
			}

			files.Add(new DeliveredFile(change, pair));
		}

		return new DeliveredChange(files, "Captured before delivery.", null);
	}

	/// <summary>Adds how the pipeline's attempt ended, with a link to its pull request when it opened one.</summary>
	/// <param name="outcome">What the pipeline reported.</param>
	/// <param name="repositoryFullName">The repository, as <c>owner/name</c>.</param>
	public DeliveredChange WithOutcome(AiChange.AiChangeOutcome outcome, string repositoryFullName)
		=> WithOutcome(
			outcome.Message,
			outcome.PullRequestNumber is { } number ? $"https://github.com/{repositoryFullName}/pull/{number}" : null);

	/// <summary>Adds how delivery ended and the pull request it produced.</summary>
	/// <param name="summary">One line saying how it ended.</param>
	/// <param name="pullRequestUrl">The pull request, or null if none was opened.</param>
	public DeliveredChange WithOutcome(string summary, string? pullRequestUrl)
		=> this with { Summary = summary, PullRequestUrl = pullRequestUrl };
}
