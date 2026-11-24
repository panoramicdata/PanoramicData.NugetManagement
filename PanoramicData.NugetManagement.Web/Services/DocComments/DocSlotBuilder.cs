namespace PanoramicData.NugetManagement.Web.Services.DocComments;

/// <summary>
/// One insertion point for a doc comment.
/// </summary>
/// <param name="Id">The number the model answers by.</param>
/// <param name="DeclLine">The 0-based line of the declaration the compiler flagged.</param>
/// <param name="InsertLine">The 0-based line the comment goes before, above any attributes.</param>
/// <param name="Indent">The declaration's own leading whitespace, reused so tabs stay tabs.</param>
/// <param name="Members">The compiler names of every member sharing the line.</param>
/// <param name="Declaration">The declaration's trimmed text.</param>
public sealed record DocSlot(int Id, int DeclLine, int InsertLine, string Indent, IReadOnlyList<string> Members, string Declaration);

/// <summary>
/// Works out where each doc comment must go so that it binds to its member.
/// </summary>
public static class DocSlotBuilder
{
	/// <summary>
	/// The slots for a file's missing comments, numbered from 1 in source order.
	/// </summary>
	/// <param name="text">The file's text.</param>
	/// <param name="targets">The diagnostics for this file.</param>
	public static IReadOnlyList<DocSlot> Build(string text, IEnumerable<MissingDocDiagnostic> targets)
	{
		var lines = text.Split('\n');
		var slots = new List<DocSlot>();

		// A type and its primary constructor share a line, so grouping by line gives one slot for both.
		foreach (var group in targets.GroupBy(t => t.Line).OrderBy(g => g.Key))
		{
			var declIndex = group.Key - 1;

			if (declIndex < 0 || declIndex >= lines.Length)
			{
				continue;
			}

			var declaration = lines[declIndex].TrimEnd('\r');

			slots.Add(new DocSlot(
				slots.Count + 1,
				declIndex,
				AboveAttributes(lines, declIndex),
				declaration[..(declaration.Length - declaration.TrimStart().Length)],
				[.. group.Select(t => t.Member).Distinct().OrderBy(m => m.Length)],
				declaration.Trim()));
		}

		return slots;
	}

	// A doc comment between an attribute list and its declaration binds to nothing (and adds CS1587), so
	// climb past multi-line attribute lists, blank lines and plain line comments to the first attribute.
	private static int AboveAttributes(string[] lines, int declIndex)
	{
		var insert = declIndex;

		while (true)
		{
			var last = PreviousCodeLine(lines, insert);

			if (last < 0 || !EndsAttributeList(lines[last]))
			{
				return insert;
			}

			var opening = OpeningLine(lines, last);

			if (opening < 0)
			{
				return insert;
			}

			insert = opening;
		}
	}

	private static bool EndsAttributeList(string line)
		=> line.TrimEnd().EndsWith(']') || line.Contains("] //", StringComparison.Ordinal);

	private static int PreviousCodeLine(string[] lines, int before)
	{
		var index = before - 1;

		while (index >= 0)
		{
			var trimmed = lines[index].Trim();

			if (trimmed.Length > 0 && (!trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal)))
			{
				break;
			}

			index--;
		}

		return index;
	}

	private static int OpeningLine(string[] lines, int closing)
	{
		var depth = 0;

		for (var k = closing; k >= 0; k--)
		{
			var line = lines[k].TrimEnd('\r');
			depth += line.Count(c => c == ']') - line.Count(c => c == '[');

			if (depth <= 0 && line.TrimStart().StartsWith('['))
			{
				return k;
			}
		}

		return -1;
	}
}
