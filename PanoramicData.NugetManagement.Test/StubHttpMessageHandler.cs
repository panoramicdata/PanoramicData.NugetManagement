using System.Net;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Returns a scripted sequence of responses, so a test can describe a flaky endpoint without a
/// network. Each request consumes the next entry; the last entry repeats once exhausted.
/// </summary>
internal sealed class StubHttpMessageHandler(params Func<HttpResponseMessage>[] responses)
	: HttpMessageHandler
{
	private int _callCount;

	/// <summary>How many requests have been made.</summary>
	public int CallCount => _callCount;

	/// <summary>A response carrying the given nuspec body.</summary>
	public static Func<HttpResponseMessage> Nuspec(string body)
		=> () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };

	/// <summary>A response with the given status and no body.</summary>
	public static Func<HttpResponseMessage> Status(HttpStatusCode code)
		=> () => new HttpResponseMessage(code);

	/// <summary>A transport failure, as a dropped connection produces.</summary>
	public static Func<HttpResponseMessage> Throws()
		=> () => throw new HttpRequestException("The connection was closed.");

	/// <inheritdoc />
	protected override Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request,
		CancellationToken cancellationToken)
	{
		var index = Math.Min(_callCount, responses.Length - 1);
		_callCount++;
		return Task.FromResult(responses[index]());
	}
}

/// <summary>
/// Hands out clients over one scripted handler, so the resolver can ask the factory exactly as it
/// does in production.
/// </summary>
internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
	/// <inheritdoc />
	public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
