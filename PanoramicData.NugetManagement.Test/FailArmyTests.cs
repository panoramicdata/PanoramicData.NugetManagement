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
			Output.WriteLine($"[{(result.Passed ? "PASS" : "FAIL")}] {result.RuleId}: {result.Message}");
			if (result.Passed)
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
			"every rule should fail against the FailArmy fixture — " +
			"if a rule passed, it means the fixture doesn't violate that rule");
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
		assessment.FailedCount.Should().Be(ruleResults.Count, "every rule should fail");
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
			});
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
