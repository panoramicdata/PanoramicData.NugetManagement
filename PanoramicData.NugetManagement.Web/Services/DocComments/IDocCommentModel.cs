namespace PanoramicData.NugetManagement.Web.Services.DocComments;

/// <summary>
/// The model, reduced to one question: text in, text out. It is given no tools and cannot act.
/// </summary>
public interface IDocCommentModel
{
	/// <summary>
	/// Asks for doc comment text.
	/// </summary>
	/// <param name="systemPrompt">The fixed instructions.</param>
	/// <param name="userPrompt">The file and the slots.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	Task<string> AskAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IDocCommentModel"/> over the application's chat model, with no tools offered.
/// </summary>
/// <param name="chat">The chat model, typically <see cref="OllamaChatModel"/>.</param>
public sealed class ChatDocCommentModel(IChatModel chat) : IDocCommentModel
{
	/// <inheritdoc />
	public async Task<string> AskAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
	{
		var turn = await chat
			.NextAsync(systemPrompt, [new AiMessage("user", userPrompt)], [], null, cancellationToken)
			.ConfigureAwait(false);

		return turn.Text ?? string.Empty;
	}
}
