using System.Net;
using Codacy.Api.Exceptions;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Recognises the Codacy answer that means "this repository was never added", whatever shape the
/// client hands it back in.
/// </summary>
/// <remarks>
/// Codacy.Api defines <see cref="CodacyNotFoundException"/>, but its Refit-generated calls do not
/// throw it: a 404 arrives as a plain <see cref="HttpRequestException"/> carrying the status code.
/// Catching only the named exception left the untracked case reported as "failed to reach Codacy",
/// which sends the reader to check a token that is working perfectly.
/// </remarks>
internal static class CodacyNotFound
{
	/// <summary>
	/// Whether the exception is Codacy's 404, i.e. it does not know the repository.
	/// </summary>
	public static bool Matches(Exception exception) => exception switch
	{
		CodacyNotFoundException => true,
		CodacyApiException codacy => codacy.StatusCode == HttpStatusCode.NotFound,
		HttpRequestException http => http.StatusCode == HttpStatusCode.NotFound,
		_ => false
	};
}
