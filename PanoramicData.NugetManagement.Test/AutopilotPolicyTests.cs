using PanoramicData.NugetManagement.Web.Services.Governance;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the Autopilot policy: the written rules that are hashed into every ledger entry, so an action
/// can be tied to the rules in force when it was taken.
/// </summary>
public class AutopilotPolicyTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _folder = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		$"policy-{Guid.NewGuid():n}");

	public void Dispose()
	{
		if (Directory.Exists(_folder))
		{
			Directory.Delete(_folder, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private string PolicyPath => Path.Combine(_folder, "autopilot-policy.json");

	private (GovernanceLedger Ledger, AutopilotPolicyStore Store) Open()
	{
		var ledger = new GovernanceLedger(Path.Combine(_folder, "ledger"));

		return (ledger, new AutopilotPolicyStore(PolicyPath, ledger));
	}

	[Fact]
	public void TheDefaultPolicy_IsOffAndDryRun_WithTheDecidedCaps()
	{
		var policy = new AutopilotPolicy();

		policy.Enabled.Should().BeFalse("nothing runs until a person says so");
		policy.DryRun.Should().BeTrue();
		policy.Caps.PushesPerDay.Should().Be(30);
		policy.Caps.PublishesPerDay.Should().Be(3);
		policy.Schedule.WindowsUtc.Should().BeNull("it may run at any time");
		policy.Eligibility.DefaultEnabled.Should().BeTrue();
		policy.Eligibility.RequireCleanTree.Should().BeTrue();
	}

	[Fact]
	public void EveryActionSwitchIsPermittedByDefault_AsDecided()
	{
		var actions = new AutopilotPolicy().Actions;

		actions.Should().BeEquivalentTo(new AutopilotActions
		{
			Push = true,
			PushDefaultBranch = true,
			Publish = true,
			WriteSecrets = true,
			EditWorkflows = true,
			EditPublishScript = true
		});
	}

	[Fact]
	public void ARepository_IsEnabledByDefault_ButNeverWhenExcluded()
	{
		var policy = new AutopilotPolicy
		{
			Eligibility = new AutopilotEligibility
			{
				DefaultEnabled = true,
				RepositoryOverrides = new Dictionary<string, bool> { ["o/forced-on"] = true }
			}
		};

		policy.IsRepositoryEnabled("o/anything", isExcluded: false).Should().BeTrue();
		policy.IsRepositoryEnabled("o/forced-on", isExcluded: true).Should().BeFalse(
			"an excluded repository is never active, whatever its switch says");
	}

	[Fact]
	public void APerRepositoryOverride_CanSwitchOneOff()
	{
		var policy = new AutopilotPolicy
		{
			Eligibility = new AutopilotEligibility
			{
				RepositoryOverrides = new Dictionary<string, bool> { ["o/careful"] = false }
			}
		};

		policy.IsRepositoryEnabled("o/careful", isExcluded: false).Should().BeFalse();
		policy.IsRepositoryEnabled("o/other", isExcluded: false).Should().BeTrue();
	}

	[Fact]
	public void DefaultEnabledFalse_MakesRepositoriesOptIn()
	{
		var policy = new AutopilotPolicy
		{
			Eligibility = new AutopilotEligibility
			{
				DefaultEnabled = false,
				RepositoryOverrides = new Dictionary<string, bool> { ["o/chosen"] = true }
			}
		};

		policy.IsRepositoryEnabled("o/chosen", isExcluded: false).Should().BeTrue();
		policy.IsRepositoryEnabled("o/other", isExcluded: false).Should().BeFalse();
	}

	[Theory]
	[InlineData(new string[0], new string[0], "TST-10", true)]
	[InlineData(new[] { "TST-10" }, new string[0], "TST-10", true)]
	[InlineData(new[] { "TST-10" }, new string[0], "CQ-06", false)]
	[InlineData(new string[0], new[] { "CQ-06" }, "CQ-06", false)]
	[InlineData(new[] { "CQ-06" }, new[] { "CQ-06" }, "CQ-06", false)]
	[InlineData(new[] { "tst-10" }, new string[0], "TST-10", true)]
	public void RuleSelection_HonoursIncludeAndExclude(string[] include, string[] exclude, string rule, bool expected)
		=> new AutopilotPolicy { Rules = new AutopilotRules { Include = include, Exclude = exclude } }
			.IsRuleEnabled(rule).Should().Be(expected);

	[Fact]
	public void RouteFor_UsesTheRulesRoute_FallingBackToTheDefaultModel()
	{
		var policy = new AutopilotPolicy
		{
			Routing = new AutopilotRouting
			{
				Default = "qwen3.8:27b",
				ByRule = new Dictionary<string, AutopilotRoute>
				{
					["TST-10"] = new() { BudgetMinutes = 40 },
					["CQ-06"] = new() { Model = "bigger:70b" }
				}
			}
		};

		policy.RouteFor("TST-10").Should().BeEquivalentTo(new AutopilotRoute { Model = "qwen3.8:27b", BudgetMinutes = 40 });
		policy.RouteFor("CQ-06").Model.Should().Be("bigger:70b");
		policy.RouteFor("PKG-12").Model.Should().Be("qwen3.8:27b");
	}

	[Fact]
	public void Hash_IsStable_AndChangesWithAnyField()
	{
		var baseline = new AutopilotPolicy();

		AutopilotPolicyStore.Hash(baseline).Should().Be(AutopilotPolicyStore.Hash(new AutopilotPolicy()));
		AutopilotPolicyStore.Hash(baseline with { Enabled = true }).Should().NotBe(AutopilotPolicyStore.Hash(baseline));
		AutopilotPolicyStore.Hash(baseline with { Caps = new AutopilotCaps { PushesPerDay = 31 } })
			.Should().NotBe(AutopilotPolicyStore.Hash(baseline));
	}

	[Fact]
	public void Store_WithNoFile_StartsOnTheDefault()
	{
		var (_, store) = Open();

		store.Current.Should().BeEquivalentTo(new AutopilotPolicy());
		store.PolicyHash.Should().Be(AutopilotPolicyStore.Hash(new AutopilotPolicy()));
	}

	[Fact]
	public void Update_WritesTheFile_RecordsTheChange_AndTheNextEntryCarriesTheNewHash()
	{
		var (ledger, store) = Open();
		var ledgerWithHash = new GovernanceLedger(Path.Combine(_folder, "ledger"), () => store.PolicyHash);

		var newHash = store.Update(new AutopilotPolicy { Enabled = true, DryRun = false }, "user", "starting the first live phase");

		File.Exists(PolicyPath).Should().BeTrue();
		var change = ledger.Query(kind: "policy_changed").Single();
		change.Actor.Should().Be("user");
		change.Data.GetProperty("reason").GetString().Should().Be("starting the first live phase");
		change.Data.GetProperty("newHash").GetString().Should().Be(newHash);
		change.Data.GetProperty("policy").GetProperty("enabled").GetBoolean().Should().BeTrue();

		ledgerWithHash.Append("autopilot", "cycle_started").PolicyHash.Should().Be(newHash);
	}

	[Fact]
	public void Update_WithoutAReason_IsRefused()
	{
		var (ledger, store) = Open();

		var act = () => store.Update(new AutopilotPolicy { Enabled = true }, "user", "  ");

		act.Should().Throw<ArgumentException>();
		store.Current.Enabled.Should().BeFalse();
		ledger.Query(kind: "policy_changed").Should().BeEmpty();
	}

	[Fact]
	public void APolicyThatWasUpdated_SurvivesARestart()
	{
		var (_, store) = Open();
		store.Update(
			new AutopilotPolicy { Enabled = true, Caps = new AutopilotCaps { PushesPerDay = 7 } },
			"user",
			"tightening");

		var (_, reopened) = Open();

		reopened.Current.Enabled.Should().BeTrue();
		reopened.Current.Caps.PushesPerDay.Should().Be(7);
		reopened.PolicyHash.Should().Be(store.PolicyHash);
	}

	[Fact]
	public void ADamagedPolicyFile_FallsBackToTheSafeDefault_AndSaysSo()
	{
		Directory.CreateDirectory(_folder);
		File.WriteAllText(PolicyPath, "{ \"enabled\": true, \"caps\": ");

		var (ledger, store) = Open();

		store.Current.Enabled.Should().BeFalse("a half-read policy must never leave Autopilot running under rules nobody wrote");
		store.Current.DryRun.Should().BeTrue();
		ledger.Query(kind: "policy_invalid").Should().ContainSingle();
	}
}
