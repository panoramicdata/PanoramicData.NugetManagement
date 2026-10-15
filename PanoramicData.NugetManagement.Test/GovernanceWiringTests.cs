using Microsoft.Extensions.DependencyInjection;
using PanoramicData.NugetManagement.Web.Services.Governance;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that the governance services resolve together in a real container. The ledger and the policy store
/// each need the other, and a registration that looks right can still fail at first use.
/// </summary>
public class GovernanceWiringTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		$"wiring-{Guid.NewGuid():n}");

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private ServiceProvider Build()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddGovernance(
			Path.Combine(_root, "ledger"),
			Path.Combine(_root, "autopilot-policy.json"),
			Path.Combine(_root, "ledger", "packets"));

		return services.BuildServiceProvider();
	}

	[Fact]
	public void Everything_Resolves_WithoutACircularDependency()
	{
		using var provider = Build();

		provider.GetRequiredService<AutopilotPolicyStore>().Should().NotBeNull();
		provider.GetRequiredService<GovernanceLedger>().Should().NotBeNull();
		provider.GetRequiredService<ReviewPacketStore>().Should().NotBeNull();
		provider.GetRequiredService<GovernanceRecorder>().Should().NotBeNull();
	}

	[Fact]
	public void ResolvingTheLedgerFirst_StillWorks()
	{
		using var provider = Build();

		var ledger = provider.GetRequiredService<GovernanceLedger>();
		ledger.Append("user", "note");

		ledger.Verify().Intact.Should().BeTrue();
	}

	[Fact]
	public void AfterThePolicyChanges_EveryEntryCarriesTheNewHash_ThroughTheContainersLedger()
	{
		using var provider = Build();
		var store = provider.GetRequiredService<AutopilotPolicyStore>();
		var ledger = provider.GetRequiredService<GovernanceLedger>();

		var hash = store.Update(new AutopilotPolicy { Enabled = true }, "user", "testing the wiring");
		var later = ledger.Append("autopilot", "cycle_started");

		later.PolicyHash.Should().Be(hash);
		ledger.Query(kind: "policy_changed").Single().PolicyHash.Should().Be(hash);
	}

	[Fact]
	public void TheServicesAreSingletons_SoOneChainIsWrittenNotSeveral()
	{
		using var provider = Build();

		provider.GetRequiredService<GovernanceLedger>().Should().BeSameAs(provider.GetRequiredService<GovernanceLedger>());
		provider.GetRequiredService<AutopilotPolicyStore>().Should().BeSameAs(provider.GetRequiredService<AutopilotPolicyStore>());
	}

	[Fact]
	public void ADamagedPolicyFile_IsRecordedInTheLedger_WhenTheContainerBuildsTheStore()
	{
		Directory.CreateDirectory(_root);
		File.WriteAllText(Path.Combine(_root, "autopilot-policy.json"), "{ not json");

		using var provider = Build();
		provider.GetRequiredService<AutopilotPolicyStore>();

		provider.GetRequiredService<GovernanceLedger>().Query(kind: "policy_invalid").Should().ContainSingle();
	}
}
