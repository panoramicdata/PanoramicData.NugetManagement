using System.Text;

namespace PanoramicData.NugetManagement.Web.Services.DocComments;

/// <summary>
/// What happened to one file.
/// </summary>
/// <param name="RelativePath">The path relative to the repository.</param>
/// <param name="Slots">How many insertion points the file had.</param>
/// <param name="Inserted">How many comments were accepted and inserted.</param>
/// <param name="Rejected">Why each unusable proposal was refused; those members stay undocumented.</param>
/// <param name="Skipped">Why the file was not processed at all, or null.</param>
/// <param name="Written">Whether the file on disk changed.</param>
public sealed record DocFileResult(string RelativePath, int Slots, int Inserted, IReadOnlyList<string> Rejected, string? Skipped, bool Written);

/// <summary>
/// Fills in missing XML doc comments with a model that only proposes text.
/// </summary>
/// <remarks>
/// The model never edits anything. Its answer is parsed down to <c>///</c> lines, each block is validated,
/// the tool inserts the survivors mechanically, and the result is refused unless stripping <c>///</c> and
/// blank lines from old and new text leaves them identical. The build afterwards, judged by
/// <see cref="DocBuildVerdict"/>, is the second check; a file is only ever written when both can still pass.
/// </remarks>
/// <param name="model">The model that proposes comment text.</param>
/// <param name="maxFileCharacters">Files longer than this are skipped rather than sent to the model.</param>
public sealed class DocCommentRemediator(IDocCommentModel model, int maxFileCharacters = 60_000)
{
	private static readonly UTF8Encoding _strictUtf8 = new(false, true);

	/// <summary>
	/// Documents what the model can usefully document, one file at a time.
	/// </summary>
	/// <param name="repositoryRoot">The clone's directory; nothing outside it is touched.</param>
	/// <param name="targets">The CS1591 diagnostics, from <see cref="Cs1591Parser"/>.</param>
	/// <param name="dryRun">When true, nothing is written.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	public async Task<IReadOnlyList<DocFileResult>> RunAsync(
		string repositoryRoot,
		IReadOnlyList<MissingDocDiagnostic> targets,
		bool dryRun,
		CancellationToken cancellationToken)
	{
		var root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		var results = new List<DocFileResult>();

		foreach (var file in targets.GroupBy(t => Path.GetFullPath(t.FilePath), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
		{
			cancellationToken.ThrowIfCancellationRequested();

			var relative = file.Key.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? file.Key[root.Length..] : file.Key;

			results.Add(file.Key.StartsWith(root, StringComparison.OrdinalIgnoreCase)
				? await ProcessAsync(file.Key, relative, file, dryRun, cancellationToken).ConfigureAwait(false)
				: Skip(relative, "file is outside the repository"));
		}

		return results;
	}

	private static DocFileResult Skip(string relative, string reason) => new(relative, 0, 0, [], reason, false);

	private async Task<DocFileResult> ProcessAsync(
		string path,
		string relative,
		IEnumerable<MissingDocDiagnostic> targets,
		bool dryRun,
		CancellationToken cancellationToken)
	{
		if (!File.Exists(path))
		{
			return Skip(relative, "file not found");
		}

		var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
		var hasBom = bytes is [0xEF, 0xBB, 0xBF, ..];

		if (!TryDecode(bytes, hasBom, out var text))
		{
			return Skip(relative, "file is not valid UTF-8");
		}

		if (GeneratedCodeDetector.Reason(relative, text) is { } generated)
		{
			return Skip(relative, generated);
		}

		if (text.Length > maxFileCharacters)
		{
			return Skip(relative, "file is too large to send to the model");
		}

		var slots = DocSlotBuilder.Build(text, targets);

		if (slots.Count == 0)
		{
			return Skip(relative, "no resolvable declarations");
		}

		var answers = await AskAsync(relative, text, slots, cancellationToken).ConfigureAwait(false);

		return answers is null
			? Skip(relative, "model unavailable or failed")
			: await ApplyAsync(path, relative, text, hasBom, slots, answers, dryRun, cancellationToken).ConfigureAwait(false);
	}

	private static bool TryDecode(byte[] bytes, bool hasBom, out string text)
	{
		try
		{
			text = _strictUtf8.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));

			return true;
		}
		catch (DecoderFallbackException)
		{
			text = string.Empty;

			return false;
		}
	}

	// Null means the model could not be reached; a file is then left exactly as it was.
	private async Task<IReadOnlyDictionary<int, IReadOnlyList<string>>?> AskAsync(
		string relative,
		string text,
		IReadOnlyList<DocSlot> slots,
		CancellationToken cancellationToken)
	{
		try
		{
			var answers = DocResponseParser.Parse(
				await model.AskAsync(DocCommentPrompt.SystemPrompt, DocCommentPrompt.BuildUser(relative, text, slots), cancellationToken).ConfigureAwait(false))
				.ToDictionary(kv => kv.Key, kv => kv.Value);

			var bad = slots.Where(s => !answers.TryGetValue(s.Id, out var block) || DocBlockValidator.Reject(block, s) is not null).ToList();

			if (bad.Count > 0)
			{
				var retry = DocResponseParser.Parse(
					await model.AskAsync(DocCommentPrompt.SystemPrompt, DocCommentPrompt.BuildUser(relative, text, bad), cancellationToken).ConfigureAwait(false));

				foreach (var slot in bad.Where(s => retry.ContainsKey(s.Id)))
				{
					answers[slot.Id] = retry[slot.Id];
				}
			}

			return answers;
		}
		catch (Exception ex) when (ex is HttpRequestException or AiModelOutputException or TimeoutException
			|| (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
		{
			return null;
		}
	}

	private static async Task<DocFileResult> ApplyAsync(
		string path,
		string relative,
		string text,
		bool hasBom,
		IReadOnlyList<DocSlot> slots,
		IReadOnlyDictionary<int, IReadOnlyList<string>> answers,
		bool dryRun,
		CancellationToken cancellationToken)
	{
		var accepted = new Dictionary<int, IReadOnlyList<string>>();
		var rejected = new List<string>();

		foreach (var slot in slots)
		{
			var reason = answers.TryGetValue(slot.Id, out var block) ? DocBlockValidator.Reject(block, slot) : "the model gave no answer";

			if (reason is null)
			{
				accepted[slot.Id] = answers[slot.Id];
			}
			else
			{
				rejected.Add($"{slot.Members[0]}: {reason}");
			}
		}

		if (accepted.Count == 0)
		{
			return new DocFileResult(relative, slots.Count, 0, rejected, null, false);
		}

		var updated = DocCommentInserter.Insert(text, slots, accepted);

		if (!DocCommentInserter.CodeUnchanged(text, updated))
		{
			return new DocFileResult(relative, slots.Count, 0, rejected, "refused: insertion would change code", false);
		}

		if (!dryRun)
		{
			var body = _strictUtf8.GetBytes(updated);
			await File.WriteAllBytesAsync(path, hasBom ? [.. Encoding.UTF8.GetPreamble(), .. body] : body, cancellationToken).ConfigureAwait(false);
		}

		return new DocFileResult(relative, slots.Count, accepted.Count, rejected, null, !dryRun);
	}
}
