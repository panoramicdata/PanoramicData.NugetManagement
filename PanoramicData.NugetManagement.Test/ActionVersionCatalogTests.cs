using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the self-updating <see cref="ActionVersionCatalog"/>: it learns the highest observed
/// version, persists it, and keeps the within-run floor stable.
/// </summary>
public class ActionVersionCatalogTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"action-versions-{Guid.NewGuid():N}.json");

	public void Dispose()
	{
		if (File.Exists(_tempFile))
		{
			File.Delete(_tempFile);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void GetFloorSpec_TakesHigherOfDefaultAndPersisted()
	{
		File.WriteAllText(_tempFile, """{ "actions/checkout": "v6" }""");
		var catalog = new ActionVersionCatalog(_tempFile);

		catalog.GetFloorSpec("actions/checkout", "v4").Should().Be("v6", "the persisted value is higher");
		catalog.GetFloorSpec("actions/checkout", "v9").Should().Be("v9", "the hardcoded default is higher");
		catalog.GetFloorSpec("actions/unknown", "v2").Should().Be("v2", "unknown actions fall back to the default");
	}

	[Fact]
	public void Observe_HigherVersion_PersistsAndFlagsBump_ButKeepsRunFloorStable()
	{
		File.WriteAllText(_tempFile, """{ "actions/checkout": "v6" }""");
		var catalog = new ActionVersionCatalog(_tempFile);

		catalog.Observe("actions/checkout", 7, "v6", "panoramicdata/Canary");

		// Within the same run, the floor is frozen.
		catalog.GetFloorSpec("actions/checkout", "v6").Should().Be("v6");
		// The bump is surfaced for the UI.
		catalog.RecentBumps.Should().ContainSingle()
			.Which.Should().BeEquivalentTo(new { Action = "actions/checkout", From = "v6", To = "v7", Repository = "panoramicdata/Canary" });
		// The learned value is persisted, so a fresh catalog (next run) adopts it.
		new ActionVersionCatalog(_tempFile).GetFloorSpec("actions/checkout", "v6").Should().Be("v7");
	}

	[Fact]
	public void Observe_NotHigher_DoesNothing()
	{
		File.WriteAllText(_tempFile, """{ "actions/checkout": "v6" }""");
		var catalog = new ActionVersionCatalog(_tempFile);

		catalog.Observe("actions/checkout", 6, "v6");
		catalog.Observe("actions/checkout", 4, "v6");

		catalog.RecentBumps.Should().BeEmpty();
		new ActionVersionCatalog(_tempFile).GetFloorSpec("actions/checkout", "v6").Should().Be("v6");
	}

	[Fact]
	public void NullPath_OperatesInMemory_WithNoWrites()
	{
		var catalog = new ActionVersionCatalog(null);
		catalog.Observe("actions/checkout", 9, "v6", "repo");

		catalog.RecentBumps.Should().ContainSingle();
		File.Exists(_tempFile).Should().BeFalse();
	}
}
