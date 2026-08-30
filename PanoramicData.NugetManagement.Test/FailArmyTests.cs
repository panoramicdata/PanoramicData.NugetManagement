using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Integration tests using a deliberately non-conformant repository fixture.
/// Every rule should FAIL when assessed against PanoramicData.NugetFailArmy.
/// </summary>
public class FailArmyTests : TestWithOutput
{
	private readonly RepositoryContext _failContext;

	/// <summary>
	/// Rules that cannot be made to fail by the fixture's files, so they are excluded from the
	/// "every applicable rule must fail" invariant:
	/// <list type="bullet">
	/// <item>
	/// PKG-05, PKG-06 and PKG-07 judge a declared version against two committed stores: a
	/// per-package published-date cache and an estate-learned floor. The fixture is a set of project
	/// files only — it cannot supply either, and it must not be made to, since seeding one inside the
	/// fixture would couple this test to Task 8's committed seed data rather than to the fixture's own
	/// files. With no cache entry and no floor for any package the fixture declares, every package
	/// reads as UNKNOWN, and unknown must never fail a repository. This is now a deterministic
	/// property of "no data supplied", not — as it was for PKG-06 alone before these rules stopped
	/// making network calls — a dependence on whatever nuget.org happens to report today.
	/// </item>
	/// <item>
	/// PKG-11 would need the fixture's own published package to be deprecated on nuget.org, which
	/// requires it to declare a PackageId naming a real deprecated package. META-01 requires the
	/// opposite — that no PackageId is declared — so the two cannot both fail against one fixture.
	/// Without a PackageId the rule falls back to the project file name, and no package is published
	/// under "PanoramicData.NugetFailArmy". Covered instead by DeprecatedPackageRuleTests.
	/// </item>
	/// <item>
	/// CI-10 requires a committed nuget-key.txt, but that filename is gitignored by this repository
	/// (by design, since committing one is exactly what the rule forbids), so the fixture can never
	/// contain it.
	/// </item>
	/// </list>
	/// Rules that report themselves as not applicable are excluded automatically via
	/// <see cref="RuleResult.IsApplicable"/>, so they need no entry here.
	/// </summary>
	// PKG-10 joins these because it is the precondition for the packaging rules rather than one of
	// them: the fixture has to declare that it publishes something before those rules apply at all,
	// and declaring it is exactly what PKG-10 asks for.
	private static readonly string[] _unfailableRuleIds = ["PKG-05", "PKG-06", "PKG-07", "CI-10", "PKG-10", "PKG-11"];

	/// <summary>
	/// Initializes a new instance of the <see cref="FailArmyTests"/> class.
	/// </summary>
	/// <param name="output">The test output helper.</param>
	public FailArmyTests(ITestOutputHelper output) : base(output)
	{
		_failContext = FailArmyFixture.CreateContext();
	}

	[Fact]
	public async Task FailArmy_AllRulesShouldFail()
	{
		var unexpectedPasses = new List<RuleResult>();

		foreach (var rule in RuleRegistry.Rules)
		{
			var result = await rule.EvaluateAsync(_failContext, CancellationToken.None);
			var outcome = !result.IsApplicable ? "N/A " : result.Passed ? "PASS" : "FAIL";
			Output.WriteLine($"[{outcome}] {result.RuleId}: {result.Message}");
			if (result.Passed
				&& result.IsApplicable
				&& !_unfailableRuleIds.Contains(result.RuleId, StringComparer.OrdinalIgnoreCase))
			{
				unexpectedPasses.Add(result);
			}
		}

		if (unexpectedPasses.Count > 0)
		{
			Output.WriteLine($"\n--- {unexpectedPasses.Count} UNEXPECTED PASSES ---");
			foreach (var p in unexpectedPasses)
			{
				Output.WriteLine($"  {p.RuleId} ({p.RuleName}): {p.Message}");
			}
		}

		unexpectedPasses.Should().BeEmpty(
			"every applicable rule should fail against the FailArmy fixture — " +
			"if an applicable rule passed, it means the fixture doesn't violate that rule");
	}

	[Fact]
	public async Task FailArmy_AllResultsShouldHaveRemediation()
	{
		foreach (var rule in RuleRegistry.Rules)
		{
			var result = await rule.EvaluateAsync(_failContext, CancellationToken.None);
			if (!result.Passed)
			{
				result.Advisory.Should().NotBeNull(
					$"Rule {result.RuleId} failed but provided no advisory guidance");
				result.Advisory!.Summary.Should().NotBeNullOrWhiteSpace(
					$"Rule {result.RuleId} failed but provided no advisory summary");
			}
		}
	}

	[Fact]
	public async Task FailArmy_ShouldNotBeCompliant()
	{
		var ruleResults = new List<RuleResult>();
		foreach (var rule in RuleRegistry.Rules)
		{
			ruleResults.Add(await rule.EvaluateAsync(_failContext, CancellationToken.None));
		}

		var assessment = new RepoAssessment
		{
			RepositoryFullName = _failContext.FullName,
			DefaultBranch = _failContext.DefaultBranch,
			AssessedAtUtc = DateTimeOffset.UtcNow,
			RuleResults = ruleResults
		};

		Output.WriteLine($"Passed: {assessment.PassedCount}/{ruleResults.Count}");
		Output.WriteLine($"Failed: {assessment.FailedCount}");
		Output.WriteLine($"Critical: {assessment.CriticalCount}, Errors: {assessment.ErrorCount}, Warnings: {assessment.WarningCount}");

		assessment.IsCompliant.Should().BeFalse();
		ruleResults
			.Where(r => r.IsApplicable)
			.Where(r => !_unfailableRuleIds.Contains(r.RuleId, StringComparer.OrdinalIgnoreCase))
			.Should().OnlyContain(r => !r.Passed, "every file-based rule should fail against the FailArmy fixture");
	}

	[Fact]
	public async Task FailArmy_RuleMessagesShouldContainActualOrExpectedText()
	{
		// Verify that configurable rules include useful diagnostic text
		var configurableRuleIds = new[] { "LIC-01", "LIC-02", "LIC-03", "HTTP-01", "README-04" };

		foreach (var ruleId in configurableRuleIds)
		{
			var rule = RuleRegistry.Rules.Single(r => r.RuleId == ruleId);
			var result = await rule.EvaluateAsync(_failContext, CancellationToken.None);
			result.Passed.Should().BeFalse($"Rule {ruleId} should fail against FailArmy");
			result.Message.Should().NotBeNullOrEmpty($"Rule {ruleId} should have a message");

			Output.WriteLine($"{ruleId}: {result.Message}");
		}
	}
}

/// <summary>
/// Creates a synthetic repository context that deliberately violates every assessment rule.
/// Simulates "panoramicdata/PanoramicData.NugetFailArmy" — the worst repo imaginable.
/// </summary>
internal static class FailArmyFixture
{
	private const string _fixtureRelativePath = "PanoramicData.NugetManagement.Test/Fixtures/PanoramicData.NugetFailArmy";

	/// <summary>
	/// Creates a RepositoryContext that fails every rule.
	/// </summary>
	/// <returns>A deliberately non-conformant RepositoryContext.</returns>
	public static RepositoryContext CreateContext()
	{
		var repoRoot = FindRepoRoot(AppContext.BaseDirectory)
			?? throw new InvalidOperationException(
				$"Could not find repository root from {AppContext.BaseDirectory}. Expected to find PanoramicData.NugetManagement.slnx in an ancestor directory.");

		var fixtureRoot = Path.Combine(repoRoot, _fixtureRelativePath.Replace('/', Path.DirectorySeparatorChar));
		if (!Directory.Exists(fixtureRoot))
		{
			throw new DirectoryNotFoundException($"FailArmy fixture repository not found at {fixtureRoot}.");
		}

		return LocalRepositoryContextFactory.Build(
			fixtureRoot,
			"panoramicdata/PanoramicData.NugetFailArmy",
			new RepoOptions
			{
				IsPackable = true,
				EnforceRequiredProperties = true
			},
			// Not on 'main', so the default-branch rule fails like everything else.
			defaultBranch: "master");
	}

	private static string? FindRepoRoot(string startDir)
	{
		var dir = startDir;
		while (dir is not null)
		{
			if (File.Exists(Path.Combine(dir, "PanoramicData.NugetManagement.slnx")))
			{
				return dir;
			}

			dir = Directory.GetParent(dir)?.FullName;
		}

		return null;
	}
}
