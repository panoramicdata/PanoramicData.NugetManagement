namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// <see cref="IAiChangeWork"/> over <see cref="AiFixSession"/>.
/// </summary>
/// <remarks>
/// The session already knows how to get a model from a task to a checked result; this only supplies the
/// task and the check. Writing the red test is checked by the model compiling it (the build check), and
/// the pipeline, not this class, judges whether it is genuinely red.
/// </remarks>
/// <param name="redTestSession">
/// The model loop that writes the reproducing test. Its toolbox may add test files and nothing else.
/// </param>
/// <param name="fixSession">
/// The model loop that makes the fix. Its toolbox may change only the files the brief named.
/// </param>
/// <param name="issueText">The report as written, for issue fixes.</param>
/// <param name="fixTask">The task for the fix step.</param>
/// <param name="systemPrompt">The system prompt.</param>
/// <param name="fixCheck">Re-evaluates the fix against the clone (a rule check, or build and tests).</param>
/// <param name="buildCheck">Checks the new test compiles.</param>
public sealed class AiSessionChangeWork(
	AiFixSession redTestSession,
	AiFixSession fixSession,
	string issueText,
	string fixTask,
	string systemPrompt,
	Func<CancellationToken, Task<AiRuleCheck>> fixCheck,
	Func<CancellationToken, Task<AiRuleCheck>> buildCheck) : IAiChangeWork
{
	private const string _filterMarker = "FILTER:";

	/// <inheritdoc />
	public async Task<string?> WriteRedTestAsync(
		AiChangeRequest request,
		string? feedback,
		CancellationToken cancellationToken)
	{
		var outcome = await redTestSession
			.RunAsync(
				new AiFixRequest(
					$"{request.Owner}/{request.Name}",
					request.Slug,
					request.Title,
					RedTestPrompt.Build(request, issueText, feedback),
					systemPrompt),
				buildCheck,
				cancellationToken)
			.ConfigureAwait(false);

		if (!outcome.Succeeded || outcome.FinishText is null)
		{
			return null;
		}

		var line = outcome.FinishText
			.Split('\n')
			.LastOrDefault(l => l.TrimStart().StartsWith(_filterMarker, StringComparison.Ordinal));

		return line?.Trim()[_filterMarker.Length..].Trim() is { Length: > 0 } filter ? filter : null;
	}

	/// <inheritdoc />
	public async Task<AiFixOutcome> FixAsync(
		AiChangeRequest request,
		string? redTestFilter,
		CancellationToken cancellationToken)
	{
		var task = redTestFilter is null
			? fixTask
			: $"{fixTask}\n\nThe test selected by `--filter {redTestFilter}` currently fails and reproduces the report. "
				+ "Change production code until it passes and every other test still passes. Do not edit that test.";

		return await fixSession
			.RunAsync(
				new AiFixRequest($"{request.Owner}/{request.Name}", request.Slug, request.Title, task, systemPrompt),
				fixCheck,
				cancellationToken)
			.ConfigureAwait(false);
	}
}
