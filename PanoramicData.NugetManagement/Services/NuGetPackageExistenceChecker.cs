using System.Net;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Asks nuget.org whether a package identifier has ever had a version published under it.
/// </summary>
/// <remarks>
/// Reads the flat container index, which lists every version including unlisted ones, so a package
/// that was published and then unlisted still counts as published. The index lags a push by a few
/// minutes, so a package pushed moments ago can briefly read as unpublished.
/// </remarks>
public class NuGetPackageExistenceChecker
{
	private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

	private readonly Dictionary<string, bool?> _cache = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Gets whether nuget.org holds any version of a package.
	/// </summary>
	/// <param name="packageId">The package identifier.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>
	/// True if a version exists, false if nuget.org says the package is unknown, and null when the
	/// answer could not be had. Null keeps a NuGet outage from being reported as a repository defect.
	/// </returns>
	public async Task<bool?> IsPublishedAsync(string packageId, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(packageId))
		{
			return null;
		}

		if (_cache.TryGetValue(packageId, out var cached))
		{
			return cached;
		}

		bool? published;
		try
		{
			var url = $"https://api.nuget.org/v3-flatcontainer/{Uri.EscapeDataString(packageId.ToLowerInvariant())}/index.json";
			using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

			published = response.StatusCode switch
			{
				HttpStatusCode.OK => true,
				HttpStatusCode.NotFound => false,
				_ => null
			};
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
		{
			published = null;
		}

		_cache[packageId] = published;
		return published;
	}
}
