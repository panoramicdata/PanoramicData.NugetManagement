using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// One run of a file that also appears somewhere else.
/// </summary>
/// <param name="StartLine">First line of the run in the measured file, from one.</param>
/// <param name="EndLine">Last line of the run in the measured file.</param>
/// <param name="PeerFile">A file the same run appears in, relative to the clone root.</param>
/// <param name="PeerStartLine">Where it starts there.</param>
public sealed record DuplicatedBlock(int StartLine, int EndLine, string PeerFile, int PeerStartLine);

/// <summary>
/// How much of one file is repeated elsewhere in the repository.
/// </summary>
/// <param name="Percent">Repeated tokens as a percentage of all the file's code tokens.</param>
/// <param name="DuplicatedLines">How many lines are part of a repeated run.</param>
/// <param name="CodeLines">How many lines hold code at all.</param>
/// <param name="Blocks">The repeated runs, in file order.</param>
public sealed record DuplicationReport(double Percent, int DuplicatedLines, int CodeLines, IReadOnlyList<DuplicatedBlock> Blocks)
{
	/// <summary>The other files that share code with this one, which are the files a fix may need to change too.</summary>
	public IReadOnlyList<string> PeerFiles
		=> [.. Blocks.Select(block => block.PeerFile).Distinct(StringComparer.OrdinalIgnoreCase)];
}

/// <summary>
/// Measures duplication in a C# clone without Codacy, so a fix can be judged before it is pushed.
/// </summary>
/// <remarks>
/// Written because the only local check a CQ-06 session had was "does it still build", which a model can
/// satisfy by joining two lines together. Codacy measures duplication over runs of tokens, ignoring
/// layout, and so does this: a run of <c>minTokens</c> tokens that appears elsewhere makes every line it
/// touches a duplicated line. Whitespace, comments and using directives do not count, which is what makes
/// a cosmetic edit score the same as no edit.
/// <para>
/// An approximation, not Codacy's number. It is meant to answer "did this change make the file less
/// repetitive", where being roughly right is enough and being checkable on a laptop in a second is the
/// point.
/// </para>
/// </remarks>
public static class DuplicationMeasurement
{
	/// <summary>The shortest run that counts as a repeat.</summary>
	public const int DefaultMinTokens = 50;

	private static readonly Regex _token = new(
		"""//[^\n]*|/\*.*?\*/|@"(?:[^"]|"")*"|\$?"(?:[^"\\\n]|\\.)*"|'(?:[^'\\]|\\.)*'|[A-Za-z_]\w*|\d[\w.]*|\S""",
		RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant,
		TimeSpan.FromSeconds(5));

	private sealed record FileTokens(string RelativePath, string[] Tokens, int[] Lines);

	/// <summary>
	/// Whether <paramref name="after"/> is a real improvement on <paramref name="before"/>.
	/// </summary>
	/// <param name="before">The file as the session found it.</param>
	/// <param name="after">The file as it now stands.</param>
	/// <remarks>
	/// A quarter off, or under ten percent. A smaller step is indistinguishable from the noise of
	/// moving a few lines about, and a file that was almost all repeats needs to end up mostly unique
	/// before anyone should believe it.
	/// </remarks>
	public static bool IsMeaningfulImprovement(DuplicationReport before, DuplicationReport after)
		=> after.Percent < 10 || after.Percent <= before.Percent * 0.75;

	/// <summary>
	/// Measures one file against every C# file in the clone.
	/// </summary>
	/// <param name="cloneRoot">The clone's root folder.</param>
	/// <param name="targetRelativePath">The file to measure, relative to the root.</param>
	/// <param name="minTokens">The shortest run that counts as a repeat.</param>
	/// <returns>The report, or null when the file does not exist or is not C#.</returns>
	public static DuplicationReport? Measure(string cloneRoot, string targetRelativePath, int minTokens = DefaultMinTokens)
	{
		var root = Path.GetFullPath(cloneRoot);
		var target = targetRelativePath.Replace('\\', '/');

		if (!target.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
			|| !File.Exists(Path.Combine(root, target)))
		{
			return null;
		}

		var files = new List<FileTokens>();

		foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
		{
			var relative = Path.GetRelativePath(root, path).Replace('\\', '/');

			if (IsExcluded(relative))
			{
				continue;
			}

			files.Add(Tokenise(relative, File.ReadAllText(path)));
		}

		var targetFile = files.FirstOrDefault(file => string.Equals(file.RelativePath, target, StringComparison.OrdinalIgnoreCase));

		if (targetFile is null || targetFile.Tokens.Length == 0)
		{
			return new DuplicationReport(0, 0, 0, []);
		}

		// Every window of minTokens tokens, by hash, across the whole repository.
		var windows = new Dictionary<long, List<(int File, int Position)>>();

		for (var f = 0; f < files.Count; f++)
		{
			var hashes = WindowHashes(files[f].Tokens, minTokens);

			for (var p = 0; p < hashes.Length; p++)
			{
				if (!windows.TryGetValue(hashes[p], out var list))
				{
					windows[hashes[p]] = list = [];
				}

				list.Add((f, p));
			}
		}

		var targetIndex = files.IndexOf(targetFile);
		var targetHashes = WindowHashes(targetFile.Tokens, minTokens);
		var duplicated = new bool[targetFile.Tokens.Length];
		var peerOfToken = new (string File, int Line)?[targetFile.Tokens.Length];

		for (var p = 0; p < targetHashes.Length; p++)
		{
			foreach (var (file, position) in windows[targetHashes[p]])
			{
				// The same place is not a repeat, and neither is an overlapping window in the same file:
				// a run only repeats itself when a whole other copy fits.
				if (file == targetIndex && Math.Abs(position - p) < minTokens)
				{
					continue;
				}

				for (var t = p; t < p + minTokens; t++)
				{
					duplicated[t] = true;
					peerOfToken[t] ??= (files[file].RelativePath, files[file].Lines[position]);
				}

				break;
			}
		}

		return Summarise(targetFile, duplicated, peerOfToken);
	}

	private static DuplicationReport Summarise(FileTokens file, bool[] duplicated, (string File, int Line)?[] peerOfToken)
	{
		var codeLines = file.Lines.Distinct().Count();
		var duplicatedLines = new HashSet<int>();
		var blocks = new List<DuplicatedBlock>();

		var start = -1;

		for (var t = 0; t <= file.Tokens.Length; t++)
		{
			var isDuplicated = t < file.Tokens.Length && duplicated[t];

			if (isDuplicated)
			{
				duplicatedLines.Add(file.Lines[t]);
				start = start < 0 ? t : start;
				continue;
			}

			if (start >= 0)
			{
				var peer = peerOfToken[start]!.Value;
				blocks.Add(new DuplicatedBlock(file.Lines[start], file.Lines[t - 1], peer.File, peer.Line));
				start = -1;
			}
		}

		// Counted in tokens, not lines: joining or splitting lines changes how many lines there are and so
		// moves a line-based figure without a single character of code changing, which is exactly the edit
		// this measure exists to see through.
		var percent = file.Tokens.Length == 0 ? 0 : 100.0 * duplicated.Count(flag => flag) / file.Tokens.Length;

		return new DuplicationReport(percent, duplicatedLines.Count, codeLines, blocks);
	}

	/// <summary>
	/// What to tell a model about a file's repeated runs, in the order it should deal with them.
	/// </summary>
	/// <param name="report">The measurement.</param>
	/// <param name="maxBlocks">How many runs to name before saying how many were left out.</param>
	public static string Describe(DuplicationReport report, int maxBlocks = 8)
	{
		var builder = new StringBuilder();

		builder.AppendLine(
			$"Measured here: {report.Percent.ToString("0", CultureInfo.InvariantCulture)}% of this file's code lines "
			+ "are part of a run that appears somewhere else.");

		foreach (var block in report.Blocks.Take(maxBlocks))
		{
			builder.AppendLine(
				$"- lines {block.StartLine}-{block.EndLine} also appear in {block.PeerFile} from line {block.PeerStartLine}");
		}

		if (report.Blocks.Count > maxBlocks)
		{
			builder.AppendLine($"- ...and {report.Blocks.Count - maxBlocks} more runs.");
		}

		return builder.ToString();
	}

	private static bool IsExcluded(string relativePath)
		=> relativePath.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
			|| relativePath.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
			|| relativePath.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
			|| relativePath.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
			|| relativePath.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)
			|| relativePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
			|| relativePath.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Splits source into code tokens and the line each one is on, leaving out comments, using
	/// directives and namespace declarations.
	/// </summary>
	private static FileTokens Tokenise(string relativePath, string source)
	{
		var tokens = new List<string>();
		var lines = new List<int>();

		var lineStarts = new List<int> { 0 };
		for (var i = 0; i < source.Length; i++)
		{
			if (source[i] == '\n')
			{
				lineStarts.Add(i + 1);
			}
		}

		// Set while skipping a directive, to what ends it: a using ends at its semicolon, and a namespace at
		// its semicolon or, when it is block-scoped, at the brace that opens it.
		var skipUntilSemicolon = false;
		var endsAtBrace = false;
		var previous = string.Empty;

		foreach (Match match in _token.Matches(source))
		{
			var value = match.Value;

			if (value.StartsWith("//", StringComparison.Ordinal) || value.StartsWith("/*", StringComparison.Ordinal))
			{
				continue;
			}

			if (skipUntilSemicolon)
			{
				skipUntilSemicolon = !(value == ";" || (endsAtBrace && value == "{"));
				previous = value;
				continue;
			}

			// A directive starts a statement; "using (" and "using var" later in a method do not.
			if ((value == "using" || value == "namespace") && previous is "" or ";" or "}" or "{")
			{
				skipUntilSemicolon = true;
				endsAtBrace = value == "namespace";
				previous = value;
				continue;
			}

			var line = lineStarts.BinarySearch(match.Index);
			line = line >= 0 ? line : ~line - 1;

			// What a literal says is not part of the shape of the code. Four REST interfaces that declare the same
			// methods and differ only in their route strings are one repeated block, and Codacy reads them so.
			var isLiteral = value[0] == '"' || value[0] == '\''
				|| value.StartsWith("@\"", StringComparison.Ordinal)
				|| value.StartsWith("$\"", StringComparison.Ordinal);

			tokens.Add(isLiteral ? "\"\"" : value);
			lines.Add(line + 1);
			previous = value;
		}

		return new FileTokens(relativePath, [.. tokens], [.. lines]);
	}

	private static long[] WindowHashes(string[] tokens, int window)
	{
		if (tokens.Length < window)
		{
			return [];
		}

		var tokenHashes = tokens.Select(StableHash).ToArray();
		var result = new long[tokens.Length - window + 1];

		for (var p = 0; p < result.Length; p++)
		{
			var hash = 1469598103934665603L;

			for (var t = p; t < p + window; t++)
			{
				hash = unchecked((hash ^ tokenHashes[t]) * 1099511628211L);
			}

			result[p] = hash;
		}

		return result;
	}

	private static long StableHash(string token)
	{
		var hash = 1469598103934665603L;

		foreach (var c in token)
		{
			hash = unchecked((hash ^ c) * 1099511628211L);
		}

		return hash;
	}
}
