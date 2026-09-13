using System.Collections.Concurrent;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Holds a repository's security report just long enough for the three SEC rules to share it.
/// </summary>
/// <remarks>
/// SEC-01, SEC-02 and SEC-03 ask Codacy the same question about the same repository within
/// milliseconds of each other, and each rule constructs its own service, so without this a sweep of
/// eighty repositories makes two hundred and forty identical searches — each of them paged.
/// <para>
/// The window is short on purpose. This is not a cache of Codacy's opinion; it is a way to ask once
/// per assessment. An entry that outlived the assessment would show the next one a stale verdict,
/// which is the failure mode the freshness work exists to avoid.
/// </para>
/// </remarks>
internal sealed class CodacySecurityMemo(TimeSpan window)
{
	/// <summary>
	/// The instance the rules share, so the saving is per repository rather than per service.
	/// </summary>
	public static CodacySecurityMemo Shared { get; } = new(TimeSpan.FromMinutes(2));

	private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

	private sealed record Entry(DateTimeOffset FetchedAt, Task<CodacySecurityReport> Report);

	/// <summary>
	/// Returns the report for <paramref name="key"/>, fetching it only if no live entry holds one.
	/// </summary>
	/// <remarks>
	/// The in-flight <see cref="Task{TResult}"/> is what gets stored, not its result, so three rules
	/// arriving together await one fetch rather than starting three. A fetch that throws is evicted
	/// before the exception propagates: caching a failure would silence the security rules for the
	/// rest of the window, turning a transient outage into a clean bill of health.
	/// </remarks>
	public async Task<CodacySecurityReport> GetOrAddAsync(
		string key,
		DateTimeOffset now,
		Func<Task<CodacySecurityReport>> fetchAsync)
	{
		while (true)
		{
			if (_entries.TryGetValue(key, out var existing))
			{
				if (now - existing.FetchedAt < window)
				{
					return await existing.Report.ConfigureAwait(false);
				}

				_entries.TryRemove(new KeyValuePair<string, Entry>(key, existing));
				continue;
			}

			var task = fetchAsync();
			var entry = new Entry(now, task);

			if (!_entries.TryAdd(key, entry))
			{
				// Another caller won the race; await theirs rather than keeping a second fetch.
				continue;
			}

			try
			{
				return await task.ConfigureAwait(false);
			}
			catch
			{
				_entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
				throw;
			}
		}
	}
}
