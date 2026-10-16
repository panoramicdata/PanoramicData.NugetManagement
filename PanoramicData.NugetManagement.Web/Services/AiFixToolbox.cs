using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// One tool invocation, as the model asked for it.
/// </summary>
/// <param name="Name">The tool's name.</param>
/// <param name="Arguments">Its arguments, by name.</param>
public sealed record AiToolCall(string Name, IReadOnlyDictionary<string, string> Arguments);

/// <summary>
/// What a tool invocation produced, in a form that goes straight back to the model.
/// </summary>
/// <param name="Content">What the model reads next.</param>
/// <param name="IsError">
/// Whether the call failed. Failure is still a result rather than an exception: the model is told what
/// went wrong so it can correct itself, which is the difference between one wasted turn and a dead run.
/// </param>
/// <param name="IsFinish">Whether the model declared itself done.</param>
public sealed record AiToolResult(string Content, bool IsError = false, bool IsFinish = false);

/// <summary>
/// The tools an AI fix session may use, and the only thing standing between the model and the
/// filesystem.
/// </summary>
/// <remarks>
/// Every path is resolved against the clone's root and refused if it escapes — refused, not sanitised:
/// quietly rewriting <c>../../etc/passwd</c> into something inside the clone would hide the fact that
/// the model tried, and a model that tried once will try again. Refusal comes back as a tool result it
/// can read.
/// <para>
/// Build and test arrive as delegates rather than a <see cref="DashboardService"/> dependency, so the
/// whole toolbox can be tested without a repository, a compiler or a network.
/// </para>
/// </remarks>
public sealed class AiFixToolbox(
	string cloneRoot,
	Func<CancellationToken, Task<string>>? build = null,
	Func<CancellationToken, Task<string>>? test = null,
	IReadOnlyCollection<string>? writablePaths = null,
	IReadOnlyCollection<string>? writableFolders = null,
	Func<CancellationToken, Task<string>>? coverage = null,
	bool newFilesOnly = false,
	Func<string, CancellationToken, Task<string?>>? afterWrite = null,
	bool offerRunTests = true,
	Func<string, CancellationToken, Task<string>>? restoreFile = null)
{
	/// <summary>
	/// What to append to a successful write's result: whether it still compiles, say.
	/// </summary>
	/// <remarks>
	/// A model that writes several files before it builds finds out about its first mistake after its
	/// fourth, and rewrites all four. Telling it in the result of the write that made the mistake costs
	/// one build and saves the rest. Null from the delegate means there is nothing to add.
	/// </remarks>
	private async Task<string> WithWriteFeedbackAsync(string message, string relativePath, CancellationToken cancellationToken)
	{
		if (afterWrite is null)
		{
			return message;
		}

		return await afterWrite(relativePath, cancellationToken).ConfigureAwait(false) is { Length: > 0 } note
			? $"{message}\n{note}"
			: message;
	}

	/// <summary>
	/// Folders this session may write anywhere under, in addition to <c>writablePaths</c>.
	/// </summary>
	/// <remarks>
	/// For work whose whole product is new files — a session writing tests cannot name them in advance, but
	/// can be confined to the test project's folder, which keeps it from "fixing" coverage by editing the
	/// production code the tests are meant to exercise.
	/// </remarks>
	private readonly string[]? _writableFolders = writableFolders?
		.Select(folder => folder.Replace('\\', '/').Trim('/') + "/")
		.ToArray();

	/// <summary>Whether this toolbox can run <paramref name="toolName"/>, which decides whether it is described to the model.</summary>
	/// <param name="toolName">The tool.</param>
	public bool Offers(string toolName) => toolName switch
	{
		"run_coverage" => coverage is not null,

		// Only a session untangling uncommitted edits has a committed version to go back to.
		"restore_file" => restoreFile is not null,

		// run_coverage already says whether the tests pass, and knows which failures were there before the
		// session began. A bare run_tests shows the model failures it did not cause and cannot fix, which
		// is how one session came to edit a library's integration tests.
		"run_tests" => coverage is null && offerRunTests,
		_ => true
	};

	/// <summary>
	/// The refusal for a path this session may not write, or null when it may.
	/// </summary>
	private AiToolResult? RefuseUnwritable(string relativePath)
	{
		if (_writable is null && _writableFolders is null)
		{
			return null;
		}

		if (_writable?.Contains(relativePath) == true)
		{
			return null;
		}

		if (_writableFolders?.Any(folder => relativePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) == true)
		{
			// New files only: a file this session created may be edited again, one that was already there
			// may not. Adding tests cannot be done by changing the tests that exist, and refusing the
			// attempt is the only guarantee that none was weakened, skipped or deleted along the way.
			var existed = File.Exists(Path.Combine(_root, relativePath))
				&& !(_originals.TryGetValue(relativePath, out var original) && original is null);

			return newFilesOnly && existed
				? Error($"'{relativePath}' already exists, and in this session existing files are read-only. "
					+ "Create a new file with a different name instead; do not edit this one.")
				: null;
		}

		var allowed = (_writable ?? []).Order(StringComparer.Ordinal)
			.Concat((_writableFolders ?? []).Select(folder => folder + "…"));

		return Error($"You may not write '{relativePath}'. This session may only write: {string.Join(", ", allowed)}.");
	}

	/// <summary>
	/// The only files this session may write, or null when it may write anywhere in the clone.
	/// </summary>
	/// <remarks>
	/// Set for a fix derived from a human-raised issue and left null for a rule-driven one, and the
	/// difference is the oracle. A rule re-evaluates for free, so a rule-driven session is bounded by
	/// something that can tell it it is wrong; an issue has no such check, so its bound is this list —
	/// the paths screened before the session was ever queued.
	/// <para>
	/// It is the containment that survives every upstream failure at once. If the analysis was fooled,
	/// the gate cleared it and the brief was wrong, a session can still only reach the handful of files
	/// a human-readable screen already accepted.
	/// </para>
	/// </remarks>
	private readonly HashSet<string>? _writable = writablePaths is null
		? null
		: new HashSet<string>(
			writablePaths.Select(path => path.Replace('\\', '/')),
			StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// How much of a file the model may see at once.
	/// </summary>
	/// <remarks>
	/// A guard on the context window, not on disk. One 2 MB generated file would fill a 131k window and
	/// leave no room for the instructions, so it is cut and said to be cut.
	/// </remarks>
	public const int MaxReadBytes = 60_000;

	/// <summary>Every tool this toolbox executes, which is also every tool described to the model.</summary>
	public static IReadOnlySet<string> ToolNames { get; } = new HashSet<string>(StringComparer.Ordinal)
	{
		"list_files", "read_file", "replace_in_file", "write_file", "run_build", "run_tests", "run_coverage", "restore_file", "finish"
	};

	private readonly string _root = Path.GetFullPath(cloneRoot);

	private readonly HashSet<string> _filesWritten = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Each written file's bytes as they were before this session first touched it, or null where it did
	/// not exist.
	/// </summary>
	private readonly Dictionary<string, byte[]?> _originals = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Puts back every file this session wrote, exactly as it was before the session touched it.
	/// </summary>
	/// <returns>How many files were restored or removed.</returns>
	/// <remarks>
	/// The alternative, discarding all local changes in the clone, also destroys whatever was already
	/// there: an earlier fix awaiting review, or a person's own edit. A failed session owes the tree
	/// only the removal of what it did itself. Bytes rather than text, so a byte-order mark or line
	/// ending survives the round trip.
	/// </remarks>
	public int Undo()
	{
		var restored = 0;

		foreach (var (relativePath, original) in _originals)
		{
			var fullPath = Path.GetFullPath(Path.Combine(_root, relativePath));

			if (original is null)
			{
				if (File.Exists(fullPath))
				{
					File.Delete(fullPath);
					restored++;
				}
			}
			else
			{
				File.WriteAllBytes(fullPath, original);
				restored++;
			}
		}

		_originals.Clear();
		_filesWritten.Clear();

		return restored;
	}

	/// <summary>Records a file's current bytes the first time the session is about to change it.</summary>
	private void Remember(string relativePath, string fullPath)
		=> _originals.TryAdd(relativePath, File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null);

	/// <summary>
	/// The files this session has written, relative to the clone root.
	/// </summary>
	/// <remarks>
	/// Kept because for some rules it is the only evidence of progress there is. A rule graded by Codacy
	/// on the published branch cannot answer whether the working tree improved, so the question asked
	/// instead is whether the model changed anything at all and left it building — and this is the first
	/// half of that.
	/// </remarks>
	public IReadOnlyCollection<string> FilesWritten => _filesWritten;

	/// <summary>
	/// Executes one tool call.
	/// </summary>
	/// <param name="call">What the model asked for.</param>
	/// <param name="cancellationToken">Signalled when the user stops the work item.</param>
	public async Task<AiToolResult> ExecuteAsync(AiToolCall call, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		try
		{
			return await DispatchAsync(call, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			// A tool that throws is a tool result the model can read and correct, not a dead session. One
			// malformed glob once ended a whole run, with the model never told what it had got wrong.
			return Error($"{call.Name} failed: {ex.Message}");
		}
	}

	private async Task<AiToolResult> DispatchAsync(AiToolCall call, CancellationToken cancellationToken)
	{
		return call.Name switch
		{
			"list_files" => ListFiles(call),
			"read_file" => await ReadFileAsync(call, cancellationToken).ConfigureAwait(false),
			"replace_in_file" => await ReplaceInFileAsync(call, cancellationToken).ConfigureAwait(false),
			"write_file" => await WriteFileAsync(call, cancellationToken).ConfigureAwait(false),
			"run_build" => await RunAsync(build, "run_build", cancellationToken).ConfigureAwait(false),
			"run_tests" => await RunAsync(test, "run_tests", cancellationToken).ConfigureAwait(false),
			"run_coverage" => await RunAsync(coverage, "run_coverage", cancellationToken).ConfigureAwait(false),
			"restore_file" => await RestoreFileAsync(call, cancellationToken).ConfigureAwait(false),
			"finish" => Finish(call),
			_ => Error(
				$"There is no tool called '{call.Name}'. The tools available are: "
				+ $"{string.Join(", ", ToolNames.Order(StringComparer.Ordinal))}.")
		};
	}

	private AiToolResult ListFiles(AiToolCall call)
	{
		var requested = call.Arguments.TryGetValue("glob", out var g) && !string.IsNullOrWhiteSpace(g)
			? g.Trim().Replace('\\', '/')
			: "*";

		var matcher = GlobToRegex(requested);

		var matches = Directory
			.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
			.Where(path => !IsUninteresting(path))
			.Select(path => Path.GetRelativePath(_root, path).Replace('\\', '/'))
			.Where(path => matcher.IsMatch(path))
			.Order(StringComparer.OrdinalIgnoreCase)
			.Take(500)
			.ToList();

		return new AiToolResult(matches.Count == 0
			? "No files matched."
			: string.Join("\n", matches));
	}

	/// <summary>
	/// A glob as a model writes it, as a regular expression over repository-relative paths.
	/// </summary>
	/// <remarks>
	/// Models write globs the way they have seen them in other tools — <c>**/*.csproj</c>,
	/// <c>src/*.cs</c>, <c>*/*.csproj</c> — and the file system's own pattern matching accepts none of
	/// the ones with a folder in them, throwing instead. So the files are enumerated plainly and matched
	/// here. A glob with no folder part matches a file name anywhere in the repository, which is what
	/// <c>*.csproj</c> has always meant to this tool.
	/// </remarks>
	private static Regex GlobToRegex(string glob)
	{
		var builder = new StringBuilder(glob.Contains('/', StringComparison.Ordinal) ? "^" : "^(.*/)?");

		for (var i = 0; i < glob.Length; i++)
		{
			switch (glob[i])
			{
				case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
					// "**/" is any number of folders, including none; a bare "**" is anything.
					if (i + 2 < glob.Length && glob[i + 2] == '/')
					{
						builder.Append("(.*/)?");
						i += 2;
					}
					else
					{
						builder.Append(".*");
						i++;
					}

					break;
				case '*':
					builder.Append("[^/]*");
					break;
				case '?':
					builder.Append("[^/]");
					break;
				default:
					builder.Append(Regex.Escape(glob[i].ToString()));
					break;
			}
		}

		return new Regex(builder.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
	}

	private async Task<AiToolResult> ReadFileAsync(AiToolCall call, CancellationToken cancellationToken)
	{
		if (!TryResolve(call, out var relativePath, out var fullPath, out var refusal))
		{
			return refusal;
		}

		if (!File.Exists(fullPath))
		{
			return Error($"There is no file at '{relativePath}'. Use list_files to see what is there.");
		}

		var content = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);

		return new AiToolResult(content.Length <= MaxReadBytes
			? content
			: content[..MaxReadBytes]
				+ $"\n\n[truncated: {relativePath} is "
				+ $"{content.Length.ToString(CultureInfo.InvariantCulture)} characters and only the first "
				+ $"{MaxReadBytes.ToString(CultureInfo.InvariantCulture)} are shown]");
	}

	/// <summary>
	/// Replaces one exact piece of a file, which costs the model a few tokens instead of the whole file.
	/// </summary>
	/// <remarks>
	/// <c>write_file</c> makes the model re-emit every line it keeps, and at local-model speeds a large
	/// file is minutes of generation for a one-line change. The match must be exact and unique: a miss or
	/// an ambiguity is an error that says so, because guessing which of several matches was meant would
	/// edit the wrong place and look like success.
	/// </remarks>
	private async Task<AiToolResult> ReplaceInFileAsync(AiToolCall call, CancellationToken cancellationToken)
	{
		if (!TryResolve(call, out var relativePath, out var fullPath, out var refusal))
		{
			return refusal;
		}

		if (RefuseUnwritable(relativePath) is { } refused)
		{
			return refused;
		}

		if (!call.Arguments.TryGetValue("old_text", out var oldText) || oldText.Length == 0)
		{
			return Error("replace_in_file needs an 'old_text' argument: the exact existing text to replace.");
		}

		if (!call.Arguments.TryGetValue("new_text", out var newText))
		{
			return Error("replace_in_file needs a 'new_text' argument: what to put in its place.");
		}

		if (!File.Exists(fullPath))
		{
			return Error($"There is no file at '{relativePath}'. Use write_file to create a new file.");
		}

		var content = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);

		// A file with CRLF line endings holds "\r\n" where the model, which only ever sees and emits "\n",
		// wrote "\n". Matching on the file's own convention is what keeps a correct multi-line match from
		// failing for a reason the model cannot see.
		var crlf = content.Contains("\r\n", StringComparison.Ordinal);
		var search = crlf ? oldText.Replace("\r\n", "\n").Replace("\n", "\r\n") : oldText;
		var replacement = crlf ? newText.Replace("\r\n", "\n").Replace("\n", "\r\n") : newText;

		var first = content.IndexOf(search, StringComparison.Ordinal);

		if (first < 0)
		{
			return Error(
				$"old_text was not found in '{relativePath}'. It must match the file exactly, including "
				+ "whitespace. Use read_file to copy the text exactly, or use write_file to replace the whole file.");
		}

		if (content.IndexOf(search, first + search.Length, StringComparison.Ordinal) >= 0)
		{
			return Error(
				$"old_text appears more than once in '{relativePath}'. Include more surrounding lines so it "
				+ "matches exactly one place.");
		}

		Remember(relativePath, fullPath);

		await WriteTextKeepingStyleAsync(
			fullPath,
			string.Concat(content.AsSpan(0, first), replacement, content.AsSpan(first + search.Length)),
			cancellationToken).ConfigureAwait(false);

		_filesWritten.Add(relativePath);

		return new AiToolResult(await WithWriteFeedbackAsync($"Replaced the text in {relativePath}.", relativePath, cancellationToken).ConfigureAwait(false));
	}

	private async Task<AiToolResult> WriteFileAsync(AiToolCall call, CancellationToken cancellationToken)
	{
		if (!TryResolve(call, out var relativePath, out var fullPath, out var refusal))
		{
			return refusal;
		}

		if (RefuseUnwritable(relativePath) is { } refused)
		{
			return refused;
		}

		if (!call.Arguments.TryGetValue("content", out var content))
		{
			return Error("write_file needs a 'content' argument holding the file's complete new text.");
		}

		var directory = Path.GetDirectoryName(fullPath);

		if (directory is { Length: > 0 })
		{
			Directory.CreateDirectory(directory);
		}

		Remember(relativePath, fullPath);

		await WriteTextKeepingStyleAsync(fullPath, content, cancellationToken).ConfigureAwait(false);

		_filesWritten.Add(relativePath);

		return new AiToolResult(await WithWriteFeedbackAsync($"Wrote {relativePath}.", relativePath, cancellationToken).ConfigureAwait(false));
	}

	/// <summary>
	/// Puts one file back as it is in the last commit, or removes it if the commit does not have it.
	/// </summary>
	/// <remarks>
	/// For a session untangling edits nobody reviewed: the quickest honest fix for a change that broke the
	/// build is often to not have made it. The file's current bytes are remembered first, so the session's
	/// own undo still returns the tree to how it stood before the session began.
	/// </remarks>
	private async Task<AiToolResult> RestoreFileAsync(AiToolCall call, CancellationToken cancellationToken)
	{
		if (restoreFile is null)
		{
			return Error("restore_file is not available for this repository.");
		}

		if (!TryResolve(call, out var relativePath, out var fullPath, out var refusal))
		{
			return refusal;
		}

		if (RefuseUnwritable(relativePath) is { } refused)
		{
			return refused;
		}

		Remember(relativePath, fullPath);

		var message = await restoreFile(relativePath, cancellationToken).ConfigureAwait(false);

		_filesWritten.Add(relativePath);

		return new AiToolResult(await WithWriteFeedbackAsync(message, relativePath, cancellationToken).ConfigureAwait(false));
	}

	/// <summary>
	/// Writes text the way the file it replaces was written: same byte-order mark, same line endings.
	/// </summary>
	/// <remarks>
	/// A model sees and emits plain text with <c>\n</c> line breaks and no byte-order mark, so a file
	/// written through it comes out different from how it went in even where not one character was meant
	/// to change. Across an estate that is thousands of lines of unrelated diff, and for a CRLF
	/// repository it is the whole file. A file that did not exist is written as given.
	/// </remarks>
	private static async Task WriteTextKeepingStyleAsync(string fullPath, string text, CancellationToken cancellationToken)
	{
		var bom = false;
		var crlf = false;

		if (File.Exists(fullPath))
		{
			var existing = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);

			bom = existing.Length >= 3 && existing[0] == 0xEF && existing[1] == 0xBB && existing[2] == 0xBF;
			crlf = Encoding.UTF8.GetString(existing).Contains("\r\n", StringComparison.Ordinal);
		}

		var normalised = text.Replace("\r\n", "\n", StringComparison.Ordinal);

		if (crlf)
		{
			normalised = normalised.Replace("\n", "\r\n", StringComparison.Ordinal);
		}

		await File.WriteAllTextAsync(fullPath, normalised, new UTF8Encoding(bom), cancellationToken).ConfigureAwait(false);
	}

	private static async Task<AiToolResult> RunAsync(
		Func<CancellationToken, Task<string>>? action,
		string toolName,
		CancellationToken cancellationToken)
		=> action is null
			? Error($"{toolName} is not available for this repository.")
			: new AiToolResult(Tail(await action(cancellationToken).ConfigureAwait(false)));

	private static AiToolResult Finish(AiToolCall call)
		=> new(
			call.Arguments.TryGetValue("summary", out var summary) && !string.IsNullOrWhiteSpace(summary)
				? summary
				: "Finished, with no summary given.",
			IsError: false,
			IsFinish: true);

	/// <summary>
	/// Resolves the call's <c>path</c> argument, refusing anything that leaves the clone.
	/// </summary>
	/// <remarks>
	/// The comparison is on the fully-resolved path, so <c>..</c>, a rooted path and a mixture of
	/// separators are all handled by the same check rather than by three string tests that each have to
	/// be right.
	/// </remarks>
	private bool TryResolve(
		AiToolCall call,
		out string relativePath,
		out string fullPath,
		out AiToolResult refusal)
	{
		relativePath = string.Empty;
		fullPath = string.Empty;

		if (!call.Arguments.TryGetValue("path", out var requested) || string.IsNullOrWhiteSpace(requested))
		{
			refusal = Error($"{call.Name} needs a 'path' argument, relative to the repository root.");
			return false;
		}

		var candidate = Path.GetFullPath(Path.Combine(_root, requested.Replace('\\', '/')));

		if (!candidate.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(candidate, _root, StringComparison.OrdinalIgnoreCase))
		{
			refusal = Error(
				$"'{requested}' is outside the repository. Paths must be relative to the repository root "
				+ "and must not leave it.");
			return false;
		}

		relativePath = Path.GetRelativePath(_root, candidate).Replace('\\', '/');
		fullPath = candidate;
		refusal = new AiToolResult(string.Empty);
		return true;
	}

	private static AiToolResult Error(string message) => new(message, IsError: true);

	/// <summary>Build and test output is long and the interesting part is at the end.</summary>
	private static string Tail(string output)
	{
		if (output.Length <= MaxReadBytes)
		{
			return output;
		}

		return "[earlier output omitted]\n" + output[^MaxReadBytes..];
	}

	/// <summary>
	/// Build output and version control are not the repository's source, and listing them buries what is.
	/// </summary>
	private static bool IsUninteresting(string path)
	{
		var normalised = path.Replace('\\', '/');

		return normalised.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
			|| normalised.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
			|| normalised.Contains("/.git/", StringComparison.OrdinalIgnoreCase);
	}
}
