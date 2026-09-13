using Codacy.Api;
using Codacy.Api.Models;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Fetches the open Codacy security findings for a repository.
/// </summary>
public interface ICodacySecurityService
{
	/// <summary>
	/// Retrieves every open security finding Codacy holds for a repository, across all priorities.
	/// </summary>
	/// <param name="apiToken">The Codacy API token.</param>
	/// <param name="organizationName">The GitHub organization name.</param>
	/// <param name="repositoryName">The repository name.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>
	/// The repository's findings. <see cref="CodacySecurityReport.IsTracked"/> is
	/// <see langword="false"/> when Codacy does not hold the repository at all.
	/// </returns>
	Task<CodacySecurityReport> GetReportAsync(
		string apiToken,
		string organizationName,
		string repositoryName,
		CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="ICodacySecurityService"/> backed by the Codacy security and risk management API.
/// </summary>
public sealed class CodacySecurityService : ICodacySecurityService
{
	/// <summary>
	/// Codacy's page cap. Asking for more is not refused, it is silently reduced, so the pager has
	/// to follow the cursor regardless.
	/// </summary>
	private const int PageSize = 100;

	/// <summary>
	/// The statuses that mean a finding is still outstanding. Closed and ignored findings are
	/// excluded at the API rather than filtered afterwards: the organization holds 6,722 closed
	/// findings against 754 open ones, and paging all of them to discard them would be absurd.
	/// </summary>
	internal static IReadOnlyList<SrmStatus> OpenStatuses { get; } =
		[SrmStatus.OnTrack, SrmStatus.DueSoon, SrmStatus.Overdue];

	private readonly CodacySecurityMemo _memo;

	/// <summary>
	/// Initializes a new instance of the <see cref="CodacySecurityService"/> class.
	/// </summary>
	public CodacySecurityService()
		: this(CodacySecurityMemo.Shared)
	{
	}

	/// <summary>
	/// Initializes a new instance sharing an explicit memo (for testing).
	/// </summary>
	internal CodacySecurityService(CodacySecurityMemo memo)
	{
		_memo = memo;
	}

	/// <inheritdoc />
	public Task<CodacySecurityReport> GetReportAsync(
		string apiToken,
		string organizationName,
		string repositoryName,
		CancellationToken cancellationToken)
		=> _memo.GetOrAddAsync(
			$"{organizationName}/{repositoryName}",
			DateTimeOffset.UtcNow,
			() => FetchAsync(apiToken, organizationName, repositoryName, cancellationToken));

	private static async Task<CodacySecurityReport> FetchAsync(
		string apiToken,
		string organizationName,
		string repositoryName,
		CancellationToken cancellationToken)
	{
		using var client = new CodacyClient(new CodacyClientOptions { ApiToken = apiToken });

		var body = new SearchSRMItems
		{
			Repositories = [repositoryName],
			Statuses = [.. OpenStatuses]
		};

		var items = await CodacySecurityPager
			.DrainAsync(
				async (cursor, token) =>
				{
					var response = await client.Security
						.SearchSecurityItemsAsync(
							Provider.Github,
							organizationName,
							body,
							cursor,
							PageSize,
							null,
							null,
							token)
						.ConfigureAwait(false);

					return ((IReadOnlyList<SrmItem>)response.Data, response.Pagination.Cursor);
				},
				cancellationToken)
			.ConfigureAwait(false);

		if (items.Count > 0)
		{
			return new CodacySecurityReport
			{
				IsTracked = true,
				Findings = [.. items.Select(CodacySecurityMapper.Map)]
			};
		}

		// An empty search says nothing about whether Codacy holds the repository: it answers 200
		// with an empty list for a name it has never heard of just as readily as for a clean one.
		// Only the repository endpoint tells them apart, and only an empty result pays for the call.
		var isTracked = await CodacyRepositoryLookup
			.IsAddedAsync(client, organizationName, repositoryName, cancellationToken)
			.ConfigureAwait(false);

		return new CodacySecurityReport { IsTracked = isTracked };
	}
}
