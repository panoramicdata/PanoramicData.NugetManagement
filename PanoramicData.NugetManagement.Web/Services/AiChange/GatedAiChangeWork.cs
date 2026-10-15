namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// An <see cref="IAiChangeWork"/> that holds the model's server only while the model is working.
/// </summary>
/// <remarks>
/// The pipeline keeps going long after the model has finished: it runs the suite, opens a pull request
/// and waits for CI, which can take half an hour. The gate that bounds how many callers are talking to the
/// server must not be held across that, or one repository's CI wait would idle the GPU for everyone else.
/// So the hold is taken around each call into the model and released the moment it returns, throws or is
/// stopped.
/// </remarks>
/// <param name="inner">The work that talks to the model.</param>
/// <param name="gate">The gate that limits concurrent callers of the model's server.</param>
public sealed class GatedAiChangeWork(IAiChangeWork inner, OllamaGate gate) : IAiChangeWork
{
	/// <inheritdoc />
	public async Task<string?> WriteRedTestAsync(
		AiChangeRequest request,
		string? feedback,
		CancellationToken cancellationToken)
	{
		using var hold = await gate.EnterAsync(cancellationToken).ConfigureAwait(false);

		return await inner.WriteRedTestAsync(request, feedback, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task<AiFixOutcome> FixAsync(
		AiChangeRequest request,
		string? redTestFilter,
		CancellationToken cancellationToken)
	{
		using var hold = await gate.EnterAsync(cancellationToken).ConfigureAwait(false);

		return await inner.FixAsync(request, redTestFilter, cancellationToken).ConfigureAwait(false);
	}
}
