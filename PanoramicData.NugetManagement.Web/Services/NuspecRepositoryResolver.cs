using System.Net;
using System.Xml;
using System.Xml.Linq;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Reads where a package's source lives from its nuspec, retrying a fetch that fails in transit.
/// </summary>
/// <remarks>
/// The nuspec's <c>repository</c> element first, because that is the publisher saying where the
/// source is. <c>projectUrl</c> is a documentation link and need not be the source at all: it is how
/// PanoramicData.EPPlus — whose nuspec correctly declares <c>panoramicdata/PanoramicData.EPPlus</c> —
/// was governed as <c>rimland/EPPlus</c>, the upstream it was forked from, for seven remediation runs.
///
/// Retries matter more than they look. Discovery asks this question once per package — a hundred-odd
/// small requests in a burst — and a single-attempt fetch turned any one of them going astray into a
/// permanent-looking claim that the nuspec declared nothing.
/// </remarks>
/// <param name="httpClientFactory">Supplies the pooled client this resolver fetches with.</param>
/// <param name="logger">The logger.</param>
public class NuspecRepositoryResolver(
	IHttpClientFactory httpClientFactory,
	ILogger<NuspecRepositoryResolver> logger)
{
	private const int MaxAttempts = 3;

	/// <summary>The name of the configured client this resolver asks for.</summary>
	public const string HttpClientName = "nuspec";

	/// <summary>
	/// The base delay between attempts, multiplied by the attempt number. Settable so tests need not
	/// wait.
	/// </summary>
	public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);

	/// <summary>
	/// Where the package's source lives, or why we cannot say.
	/// </summary>
	/// <param name="packageId">The package identifier.</param>
	/// <param name="version">The version whose nuspec to read.</param>
	/// <param name="projectUrl">The package's project URL, used only when the nuspec declares nothing.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	public async Task<RepositoryResolution> ResolveAsync(
		string packageId,
		string version,
		string? projectUrl,
		CancellationToken cancellationToken)
	{
		var id = packageId.ToLowerInvariant();
		var url = $"https://api.nuget.org/v3-flatcontainer/{id}/{version.ToLowerInvariant()}/{id}.nuspec";

		using var client = httpClientFactory.CreateClient(HttpClientName);

		string? nuspec = null;
		string? lastError = null;

		for (var attempt = 1; attempt <= MaxAttempts; attempt++)
		{
			try
			{
				using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

				// A 404 is the source saying there is nothing there. Asking twice more cannot change it,
				// and treating it as flaky would spend three requests on every unlisted package.
				if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
				{
					nuspec = null;
					lastError = null;
					break;
				}

				response.EnsureSuccessStatusCode();
				nuspec = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
				lastError = null;
				break;
			}
			catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
				&& !cancellationToken.IsCancellationRequested)
			{
				lastError = ex.Message;
				logger.LogDebug(
					ex,
					"Attempt {Attempt} of {MaxAttempts} to read the nuspec for {PackageId} {Version} failed",
					attempt,
					MaxAttempts,
					packageId,
					version);

				if (attempt < MaxAttempts && RetryDelay > TimeSpan.Zero)
				{
					await Task.Delay(RetryDelay * attempt, cancellationToken).ConfigureAwait(false);
				}
			}
		}

		if (lastError is not null)
		{
			return RepositoryResolution.LookupFailed(lastError);
		}

		var declared = nuspec is null ? null : ReadRepositoryUrl(nuspec, packageId, version);

		return GitHubRepositoryUrl.Normalize(declared) is { } fromNuspec
			? RepositoryResolution.Resolved(fromNuspec)
			: GitHubRepositoryUrl.Normalize(projectUrl) is { } fromProject
				? RepositoryResolution.Resolved(fromProject)
				: RepositoryResolution.NotDeclared();
	}

	private string? ReadRepositoryUrl(string nuspec, string packageId, string version)
	{
		try
		{
			return XDocument.Parse(nuspec)
				.Descendants()
				.FirstOrDefault(element =>
					string.Equals(element.Name.LocalName, "repository", StringComparison.OrdinalIgnoreCase))
				?.Attribute("url")?.Value;
		}
		catch (XmlException ex)
		{
			// Malformed XML is the publisher's, not ours, and is a fact about the nuspec rather than a
			// failure to read it: there is nothing to retry.
			logger.LogDebug(ex, "The nuspec for {PackageId} {Version} is not well-formed XML", packageId, version);
			return null;
		}
	}
}
