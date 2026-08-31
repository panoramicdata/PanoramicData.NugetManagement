using Microsoft.Extensions.Logging.Abstractions;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that reading the cached rows is safe while work is writing to them.
/// </summary>
/// <remarks>
/// The tree is rebuilt from these rows on the UI thread while up to twenty runner threads report
/// progress and upsert rows. A reader handed the cache's own list sees
/// <see cref="InvalidOperationException"/> the moment a writer adds or removes one — which kills the
/// Blazor circuit, not just the render.
/// </remarks>
public class DashboardCacheConcurrencyTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _cacheFile = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		$"cache-{Guid.NewGuid():n}.json");

	public void Dispose()
	{
		if (File.Exists(_cacheFile))
		{
			File.Delete(_cacheFile);
		}

		GC.SuppressFinalize(this);
	}

	private DashboardCacheService NewCache()
		=> new(NullLogger<DashboardCacheService>.Instance, _cacheFile);

	private static RepositoryDashboardRow Row(string name) => new()
	{
		RepositoryFullName = $"panoramicdata/{name}",
		Organization = "panoramicdata"
	};

	/// <summary>
	/// Deterministic rather than timed: an upsert is interleaved into a live enumeration at a known
	/// point. A racing-threads version of this passes or fails on timing, which is worse than no test —
	/// what matters is whether the hazard exists at all, and that is a question about aliasing.
	/// </summary>
	[Fact]
	public void EnumeratingTheRows_WhileAnUpsertAddsOne_DoesNotThrow()
	{
		var cache = NewCache();
		cache.SetRows([.. Enumerable.Range(0, 5).Select(i => Row($"Repo{i}"))]);

		var rows = cache.GetCachedRows();
		rows.Should().NotBeNull();

		var act = () =>
		{
			var seen = 0;

			foreach (var row in rows!)
			{
				seen++;

				if (seen == 1)
				{
					// A row the cache has not seen, so the upsert appends rather than replaces. Appending
					// is what invalidates an enumerator — and the tree build is one long enumeration of
					// these rows on the UI thread while runner threads upsert.
					cache.UpsertRow(Row("AddedMidEnumeration"));
				}
			}
		};

		act.Should().NotThrow<InvalidOperationException>(
			"the caller is handed the cache's own list, so an upsert during a render's walk over it "
			+ "throws Collection was modified — which kills the circuit, not just the render");
	}

	[Fact]
	public void TheRowsHandedOut_AreNotTheCachesOwnList()
	{
		var cache = NewCache();
		cache.SetRows([Row("One")]);

		var rows = cache.GetCachedRows();
		rows!.Add(Row("AddedByACaller"));

		cache.GetCachedRows()!.Select(r => r.RepositoryFullName)
			.Should().Equal(["panoramicdata/One"],
				"a caller adding to what it was handed must not silently change the estate");
	}
}
