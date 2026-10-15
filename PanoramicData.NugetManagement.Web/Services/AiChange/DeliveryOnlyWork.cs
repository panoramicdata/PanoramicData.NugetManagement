namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// The <see cref="IAiChangeWork"/> for a pipeline that is only delivering edits already made.
/// </summary>
/// <remarks>
/// <see cref="AiChangePipeline.DeliverAsync"/> never asks the model for anything, so this exists to say so
/// loudly if that ever changes, rather than to do work.
/// </remarks>
public sealed class DeliveryOnlyWork : IAiChangeWork
{
	/// <inheritdoc />
	public Task<string?> WriteRedTestAsync(AiChangeRequest request, string? feedback, CancellationToken cancellationToken)
		=> throw new InvalidOperationException("A delivery-only pipeline has no model to write a test.");

	/// <inheritdoc />
	public Task<AiFixOutcome> FixAsync(AiChangeRequest request, string? redTestFilter, CancellationToken cancellationToken)
		=> throw new InvalidOperationException("A delivery-only pipeline has no model to make a fix.");
}
