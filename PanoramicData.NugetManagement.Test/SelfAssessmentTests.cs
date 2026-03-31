using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Integration tests that assess THIS repository (PanoramicData.NugetManagement)
/// against its own rules. Eating our own dog food.
/// </summary>
public class SelfAssessmentTests : TestWithOutput
{
	private readonly RepositoryContext _context;

	/// <summary>
	/// Initializes a new instance of the <see cref="SelfAssessmentTests"/> class.
	/// </summary>
	/// <param name="output">The test output helper.</param>
	public SelfAssessmentTests(ITestOutputHelper output) : base(output)
	{
		// Walk up from the test assembly output directory to find the repo root.
		// Typical output path: bin/Debug/net10.0/
		var testAssemblyDir = AppContext.BaseDirectory;
		var repoRoot = FindRepoRoot(testAssemblyDir)
			?? throw new InvalidOperationException(
				$"Could not find repository root from {testAssemblyDir}. " +
				"Expected to find PanoramicData.NugetManagement.slnx in an ancestor directory.");

		_context = LocalRepositoryContextFactory.Build(
			repoRoot,
			"panoramicdata/PanoramicData.NugetManagement",
			excludedPathPrefixes: ["PanoramicData.NugetManagement.Test/Fixtures/"]);
	}

	[Fact]
	public async Task SelfAssessment_AllRulesShouldPass()
	{
		var failures = new List<RuleResult>();

		foreach (var rule in RuleRegistry.Rules)
		{
			var result = await rule.EvaluateAsync(_context, CancellationToken.None);
			Output.WriteLine($"[{(result.Passed ? "PASS" : "FAIL")}] {result.RuleId}: {result.Message}");
			if (!result.Passed)
			{
				failures.Add(result);
			}
		}

		if (failures.Count > 0)
		{
			Output.WriteLine($"\n--- {failures.Count} FAILURES ---");
			foreach (var f in failures)
			{
				Output.WriteLine($"  [{f.Severity}] {f.RuleId} ({f.RuleName}): {f.Message}");
				if (f.Remediation is not null)
				{
					Output.WriteLine($"    Fix: {f.Remediation}");
				}
			}
		}

		failures.Should().BeEmpty("this repository should pass all of its own rules");
	}

	[Fact]
	public async Task SelfAssessment_ErrorRulesShouldAllPass()
	{
		var errorRules = RuleRegistry.Rules
			.Where(r => r.Severity == AssessmentSeverity.Error)
			.ToList();

		var failures = new List<RuleResult>();
		foreach (var rule in errorRules)
		{
			var result = await rule.EvaluateAsync(_context, CancellationToken.None);
			if (!result.Passed)
			{
				failures.Add(result);
			}
		}

		failures.Should().BeEmpty("all Error-severity rules must pass on this repository");
	}

	[Fact]
	public void SelfAssessment_ContextShouldHaveExpectedFiles()
	{
		_context.FileExists("README.md").Should().BeTrue();
		_context.FileExists("LICENSE").Should().BeTrue();
		_context.FileExists(".editorconfig").Should().BeTrue();
		_context.FileExists(".gitignore").Should().BeTrue();
		_context.FileExists("Directory.Build.props").Should().BeTrue();
		_context.FileExists("Directory.Packages.props").Should().BeTrue();
		_context.FileExists("version.json").Should().BeTrue();
		_context.FileExists("global.json").Should().BeTrue();
		_context.FileExists("SECURITY.md").Should().BeTrue();
		_context.FileExists("CONTRIBUTING.md").Should().BeTrue();
		_context.FileExists("Publish.ps1").Should().BeTrue();
		_context.FileExists(".github/workflows/ci.yml").Should().BeTrue();
		_context.FileExists(".github/dependabot.yml").Should().BeTrue();
	}

	[Fact]
	public async Task SelfAssessment_RepoAssessmentShouldBeCompliant()
	{
		var ruleResults = new List<RuleResult>();
		foreach (var rule in RuleRegistry.Rules)
		{
			ruleResults.Add(await rule.EvaluateAsync(_context, CancellationToken.None));
		}

		var assessment = new RepoAssessment
		{
			RepositoryFullName = _context.FullName,
			DefaultBranch = _context.DefaultBranch,
			AssessedAtUtc = DateTimeOffset.UtcNow,
			RuleResults = ruleResults
		};

		Output.WriteLine($"Passed: {assessment.PassedCount}/{ruleResults.Count}");
		Output.WriteLine($"Errors: {assessment.ErrorCount}, Warnings: {assessment.WarningCount}, Info: {assessment.InfoCount}");
		assessment.IsCompliant.Should().BeTrue("the repository should have zero Error-severity failures");
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
