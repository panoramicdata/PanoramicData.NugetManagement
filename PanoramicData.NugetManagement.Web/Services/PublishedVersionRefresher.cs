using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Brings a row's published package versions up to date from nuget.org.
/// </summary>
/// <remarks>
/// Package discovery used to be the only thing that ever wrote a published version, and Re-assess
/// deliberately skips discovery to avoid re-reading the whole package list. CI-11 was therefore
/// comparing a tag pushed minutes ago against a version read hours ago, reporting a released package
/// as never published and refusing to change its mind however many times Re-assess was pressed —
/// nothing in that path had asked NuGet anything. Refreshing one version per package is not
/// re-reading the package list, so the distinction between Rediscover and Re-assess survives.
/// </remarks>
public class PublishedVersionRefresher
{
	private readonly IPublishedVersionSource _source;

	/// <summary>
	/// Initializes a new instance of the <see cref="PublishedVersionRefresher"/> class.
	/// </summary>
	/// <param name="source">Reads the newest published version of a package.</param>
	public PublishedVersionRefresher(IPublishedVersionSource source)
	{
		_source = source;
	}

	/// <summary>
	/// Re-reads each of the row's package versions from nuget.org, leaving any it cannot read as it
	/// found them.
	/// </summary>
	/// <param name="row">The repository whose packages should be refreshed.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	public async Task RefreshAsync(RepositoryDashboardRow row, CancellationToken cancellationToken)
	{
		foreach (var package in row.Packages)
		{
			string? latest;
			try
			{
				latest = await _source
					.GetLatestPublishedVersionAsync(package.PackageId, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// A version known a minute ago beats no version at all: blanking it would turn a
				// transient nuget.org failure into "this package has never been published", which is
				// CI-11's loudest finding and its least true.
				continue;
			}

			if (latest is not null)
			{
				package.LatestVersion = latest;
			}
		}
	}
}
