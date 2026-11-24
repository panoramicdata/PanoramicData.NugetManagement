namespace PanoramicData.NugetManagement.Web.Services.DocComments;

/// <summary>
/// Inserts already-validated doc comments into source text. The model's words go in as <c>///</c> lines
/// and nothing else: no code is generated, moved or removed.
/// </summary>
public static class DocCommentInserter
{
	/// <summary>
	/// The text with each answered slot's comment inserted.
	/// </summary>
	/// <param name="text">The original text.</param>
	/// <param name="slots">The slots, from <see cref="DocSlotBuilder"/>.</param>
	/// <param name="docs">The accepted <c>///</c> lines by slot id; a slot with no entry stays undocumented.</param>
	public static string Insert(string text, IReadOnlyList<DocSlot> slots, IReadOnlyDictionary<int, IReadOnlyList<string>> docs)
	{
		var lines = text.Split('\n').ToList();

		// Bottom-up, so earlier insertion indices stay valid.
		foreach (var slot in slots.OrderByDescending(s => s.InsertLine))
		{
			if (!docs.TryGetValue(slot.Id, out var doc))
			{
				continue;
			}

			// Each added line takes the line ending of the declaration it documents.
			var eol = lines[slot.DeclLine].EndsWith('\r') ? "\r" : string.Empty;
			var block = doc.Select(d => slot.Indent + d + eol).ToList();

			// Separate from the previous member unless that is already blank or an opening brace.
			if (slot.InsertLine > 0)
			{
				var previous = lines[slot.InsertLine - 1].Trim();

				if (previous.Length > 0 && previous != "{")
				{
					block.Insert(0, eol);
				}
			}

			lines.InsertRange(slot.InsertLine, block);
		}

		return string.Join('\n', lines);
	}

	/// <summary>
	/// Whether the two texts differ only by <c>///</c> lines and blank lines: every line of code matches, in order.
	/// </summary>
	/// <param name="original">The text before insertion.</param>
	/// <param name="updated">The text after insertion.</param>
	public static bool CodeUnchanged(string original, string updated)
		=> CodeLines(original).SequenceEqual(CodeLines(updated), StringComparer.Ordinal);

	private static IEnumerable<string> CodeLines(string text)
		=> text.Split('\n')
			.Select(l => l.TrimEnd('\r'))
			.Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("///", StringComparison.Ordinal));
}
