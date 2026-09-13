using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests the record of which rules are deliberately waived for which repository. A waiver is a
/// governance decision, so it is committed beside the solution and reviewed like any other change —
/// and it has to carry a reason, or the board fills up with silences nobody can account for.
/// </summary>
public class RuleWaiverCatalogTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _directory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	[Fact]
	public void ARepositoryWithNoWaiversShouldHaveNone()
		=> new RuleWaiverCatalog(null).For("panoramicdata/LanSweeper.Api").Should().BeEmpty();

	[Fact]
	public void AWaivedRuleShouldBeReturnedForItsRepository()
	{
		var catalog = CatalogFrom(
			"""
			{
			  "panoramicdata/LanSweeper.Api": [
			    { "ruleId": "HTTP-01", "reason": "Talks to one GraphQL endpoint." }
			  ]
			}
			""");

		var waivers = catalog.For("panoramicdata/LanSweeper.Api");

		waivers.Should().ContainSingle();
		waivers[0].RuleId.Should().Be("HTTP-01");
		waivers[0].Reason.Should().Be("Talks to one GraphQL endpoint.");
	}

	[Fact]
	public void AWaiverShouldCarryWhoRecordedItAndWhen()
	{
		// A waiver nobody signed is a waiver nobody can be asked about.
		var catalog = CatalogFrom(
			"""
			{
			  "panoramicdata/LanSweeper.Api": [
			    {
			      "ruleId": "HTTP-01",
			      "reason": "Talks to one GraphQL endpoint.",
			      "waivedBy": "david.bond@panoramicdata.com",
			      "waivedOnUtc": "2026-09-13T00:00:00Z"
			    }
			  ]
			}
			""");

		var waiver = catalog.For("panoramicdata/LanSweeper.Api").Single();

		waiver.WaivedBy.Should().Be("david.bond@panoramicdata.com");
		waiver.WaivedOnUtc.Should().Be(new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero));
	}

	[Fact]
	public void ARepositoryShouldMatchRegardlessOfCase()
	{
		// Full names reach this from GitHub, from a dashboard row and from a hand-edited file, and
		// nothing makes the three agree on casing.
		var catalog = CatalogFrom(
			"""
			{
			  "PanoramicData/LanSweeper.Api": [
			    { "ruleId": "HTTP-01", "reason": "Talks to one GraphQL endpoint." }
			  ]
			}
			""");

		catalog.For("panoramicdata/lansweeper.api").Should().ContainSingle();
	}

	[Fact]
	public void AnUnlistedRepositoryShouldHaveNoWaivers()
	{
		var catalog = CatalogFrom(
			"""
			{
			  "panoramicdata/LanSweeper.Api": [
			    { "ruleId": "HTTP-01", "reason": "Talks to one GraphQL endpoint." }
			  ]
			}
			""");

		catalog.For("panoramicdata/Athonet.Api").Should().BeEmpty();
	}

	[Fact]
	public void AWaiverWithNoReasonShouldBeRefused()
	{
		// The whole point of writing waivers down is that each one says why. One without a reason is
		// the silent suppression this file exists to replace, so it must not take effect.
		var catalog = CatalogFrom(
			"""
			{
			  "panoramicdata/LanSweeper.Api": [
			    { "ruleId": "HTTP-01" }
			  ]
			}
			""");

		catalog.For("panoramicdata/LanSweeper.Api").Should().BeEmpty();
		catalog.LoadFailed.Should().BeTrue();
		catalog.LoadFailure.Should().Contain("HTTP-01");
	}

	[Fact]
	public void AWaiverWithNoReasonShouldNotDiscardItsNeighbours()
	{
		// One bad entry is a bad entry, not grounds to drop every waiver in the file.
		var catalog = CatalogFrom(
			"""
			{
			  "panoramicdata/LanSweeper.Api": [
			    { "ruleId": "HTTP-01" },
			    { "ruleId": "CQ-05", "reason": "Legacy debt, tracked separately." }
			  ]
			}
			""");

		catalog.For("panoramicdata/LanSweeper.Api").Single().RuleId.Should().Be("CQ-05");
	}

	[Fact]
	public void AnUnreadableFileShouldBeReportedRatherThanThrown()
	{
		var catalog = CatalogFrom("{ this is not json");

		catalog.LoadFailed.Should().BeTrue();
		catalog.For("panoramicdata/LanSweeper.Api").Should().BeEmpty();
	}

	[Fact]
	public void AMissingFileShouldNotCountAsAFailure()
	{
		// Nothing to read is the normal state of an estate that waives nothing.
		var catalog = new RuleWaiverCatalog(Path.Combine(_directory, RuleWaiverCatalog.FileName));

		catalog.LoadFailed.Should().BeFalse();
	}

	[Fact]
	public void TheCommittedWaiversFileShouldBeUsable()
	{
		// The file is edited by hand, and a waiver that fails to load waives nothing while looking
		// exactly like a repository nobody has excused. This is the only thing that would notice.
		var path = RepositoryRootFile.Resolve(RuleWaiverCatalog.FileName);
		path.Should().NotBeNull("the tests run from inside the repository");

		var catalog = new RuleWaiverCatalog(path);

		catalog.LoadFailure.Should().BeNull();
	}

	private RuleWaiverCatalog CatalogFrom(string json)
	{
		Directory.CreateDirectory(_directory);
		var path = Path.Combine(_directory, RuleWaiverCatalog.FileName);
		File.WriteAllText(path, json);
		return new RuleWaiverCatalog(path);
	}

	public void Dispose()
	{
		GC.SuppressFinalize(this);

		if (Directory.Exists(_directory))
		{
			Directory.Delete(_directory, recursive: true);
		}
	}
}
