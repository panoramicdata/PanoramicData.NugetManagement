using System.Globalization;
using System.Text;

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
	Func<CancellationToken, Task<string>>? test = null)
{
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
		"list_files", "read_file", "write_file", "run_build", "run_tests", "finish"
	};

	private readonly string _root = Path.GetFullPath(cloneRoot);

	/// <summary>
	/// Executes one tool call.
	/// </summary>
	/// <param name="call">What the model asked for.</param>
	/// <param name="cancellationToken">Signalled when the user stops the work item.</param>
	public async Task<AiToolResult> ExecuteAsync(AiToolCall call, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		return call.Name switch
		{
			"list_files" => ListFiles(call),
			"read_file" => await ReadFileAsync(call, cancellationToken).ConfigureAwait(false),
			"write_file" => await WriteFileAsync(call, cancellationToken).ConfigureAwait(false),
			"run_build" => await RunAsync(build, "run_build", cancellationToken).ConfigureAwait(false),
			"run_tests" => await RunAsync(test, "run_tests", cancellationToken).ConfigureAwait(false),
			"finish" => Finish(call),
			_ => Error(
				$"There is no tool called '{call.Name}'. The tools available are: "
				+ $"{string.Join(", ", ToolNames.Order(StringComparer.Ordinal))}.")
		};
	}

	private AiToolResult ListFiles(AiToolCall call)
	{
		var glob = call.Arguments.TryGetValue("glob", out var g) && !string.IsNullOrWhiteSpace(g)
			? g
			: "*";

		var matches = Directory
			.EnumerateFiles(_root, glob, SearchOption.AllDirectories)
			.Where(path => !IsUninteresting(path))
			.Select(path => Path.GetRelativePath(_root, path).Replace('\\', '/'))
			.Order(StringComparer.OrdinalIgnoreCase)
			.Take(500)
			.ToList();

		return new AiToolResult(matches.Count == 0
			? "No files matched."
			: string.Join("\n", matches));
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

	private async Task<AiToolResult> WriteFileAsync(AiToolCall call, CancellationToken cancellationToken)
	{
		if (!TryResolve(call, out var relativePath, out var fullPath, out var refusal))
		{
			return refusal;
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

		await File.WriteAllTextAsync(fullPath, content, cancellationToken).ConfigureAwait(false);

		return new AiToolResult($"Wrote {relativePath}.");
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
