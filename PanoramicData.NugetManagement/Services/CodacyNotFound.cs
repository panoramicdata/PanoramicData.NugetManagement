using System.Net;
using Codacy.Api.Exceptions;
using Refit;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Recognises the Codacy answer that means "this repository was never added", whatever shape the
/// client hands it back in.
/// </summary>
/// <remarks>
/// Codacy.Api defines <see cref="CodacyNotFoundException"/>, but its Refit-generated calls never
/// throw it: a 404 arrives as a <see cref="ApiException"/>, which derives from Refit's own base
/// rather than from <see cref="HttpRequestException"/>. Catching only the named exception left an
/// untracked repository reported as "failed to reach Codacy", which sends the reader off to check a
/// token that is working perfectly.
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
		ApiException refit => refit.StatusCode == HttpStatusCode.NotFound,
		HttpRequestException http => http.StatusCode == HttpStatusCode.NotFound,
		_ => false
	};
}
