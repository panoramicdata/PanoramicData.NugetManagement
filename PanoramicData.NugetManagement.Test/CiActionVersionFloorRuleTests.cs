using System.Text.Json;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for CI-12, the generic action version floor: every action in every workflow is held to the
/// best version the organization uses anywhere, except the few actions a bespoke rule already owns.
/// </summary>
[Collection(ActionVersionCatalogCollection.Name)]
public class CiActionVersionFloorRuleTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private const string _ruleId = "CI-12";

	private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"action-versions-{Guid.NewGuid():N}.json");

	/// <summary>
	/// A catalog seeded with the given floors, installed as the process-wide default for the duration
	/// of a test. A file is used rather than <c>Observe</c> because the floor a rule compares against
	/// is frozen at load time — observations only raise it for the next run.
	/// </summary>
	private ActionVersionCatalog SeedCatalog(Dictionary<string, string> floors)
	{
		File.WriteAllText(_tempFile, JsonSerializer.Serialize(floors));
		var catalog = new ActionVersionCatalog(_tempFile);
		ActionVersionCatalog.Default = catalog;
		return catalog;
	}

	/// <inheritdoc />
	public void Dispose()
	{
		ActionVersionCatalog.Default = new ActionVersionCatalog(null);
		if (File.Exists(_tempFile))
		{
			File.Delete(_tempFile);
		}

		GC.SuppressFinalize(this);
	}

	private static RepositoryContext Ctx(params (string Path, string Content)[] workflows) => new()
	{
		FullName = "panoramicdata/Sample",
		Name = "Sample",
		DefaultBranch = "main",
		CurrentBranch = "main",
		Options = new RepoOptions(),
		FilePaths = [.. workflows.Select(w => w.Path)],
		FileContents = workflows.ToDictionary(w => w.Path, w => w.Content)
	};

	private static (string, string) CodeQl(string version) =>
		(".github/workflows/codeql.yml",
			$"jobs:\n  analyze:\n    steps:\n      - uses: github/codeql-action/init@{version}\n"
			+ $"      - uses: github/codeql-action/analyze@{version}\n");

	private static IRule Rule() => RuleRegistry.Rules.Single(r => r.RuleId == _ruleId);

	private static Task<RuleResult> Evaluate(RepositoryContext context)
		=> Rule().EvaluateAsync(context, TestContext.Current.CancellationToken);

	[Fact]
	public async Task Fails_WhenAnActionIsBelowTheLearnedFloor()
	{
		SeedCatalog(new() { ["github/codeql-action"] = "v4" });

		var result = await Evaluate(Ctx(CodeQl("v2")));

		result.Passed.Should().BeFalse("codeql-action@v2 is behind the v4 we use elsewhere");
		result.Message.Should().Contain("github/codeql-action");
		result.Advisory.Should().NotBeNull();
	}

	[Theory]
	[InlineData("v4")] // exactly the floor
	[InlineData("v5")] // ahead of it — being in front is not a failure
	public async Task Passes_AtOrAboveTheFloor(string version)
	{
		SeedCatalog(new() { ["github/codeql-action"] = "v4" });

		var result = await Evaluate(Ctx(CodeQl(version)));

		result.Passed.Should().BeTrue($"codeql-action@{version} is at or above the floor");
	}

	[Fact]
	public async Task Passes_WhenNoFloorHasBeenLearnedYet()
	{
		SeedCatalog([]);

		var result = await Evaluate(Ctx(CodeQl("v2")));

		result.Passed.Should().BeTrue(
			"with nothing to compare against, an action cannot be shown to be behind");
	}

	[Fact]
	public async Task IgnoresActionsAnotherRuleAlreadyGoverns()
	{
		SeedCatalog(new() { ["actions/checkout"] = "v7" });

		var result = await Evaluate(Ctx((".github/workflows/ci.yml",
			"jobs:\n  build:\n    steps:\n    - uses: actions/checkout@v3\n")));

		result.Passed.Should().BeTrue(
			"CI-05 owns actions/checkout, and reporting it twice would double up the failure and the fix");
		result.IsApplicable.Should().BeFalse(
			"with nothing left to judge, this rule must not report the repository as compliant on the "
			+ "strength of a check it never made");
	}

	[Fact]
	public async Task SkipsAnActionPinnedToACommitSha()
	{
		SeedCatalog(new() { ["github/codeql-action"] = "v4" });

		var result = await Evaluate(Ctx((".github/workflows/codeql.yml",
			"jobs:\n  analyze:\n    steps:\n      - uses: github/codeql-action/init@a1b2c3d4e5f60718293a4b5c6d7e8f9012345678\n")));

		result.Passed.Should().BeTrue(
			"a SHA pin states no major version, so nothing can be proven behind — and rewriting it "
			+ "would replace a deliberate pin with a floating tag");
	}

	[Fact]
	public async Task LowestUsageDecides_WhenOneWorkflowLagsBehind()
	{
		SeedCatalog(new() { ["github/codeql-action"] = "v4" });

		var result = await Evaluate(Ctx(
			CodeQl("v4"),
			(".github/workflows/scheduled.yml",
				"jobs:\n  scan:\n    steps:\n      - uses: github/codeql-action/init@v3\n")));

		result.Passed.Should().BeFalse("one workflow left behind is still work to do");
	}

	[Fact]
	public async Task NotApplicable_WhenTheRepositoryHasNoWorkflows()
	{
		SeedCatalog(new() { ["github/codeql-action"] = "v4" });

		var result = await Evaluate(Ctx((".gitignore", "bin/\n")));

		result.Passed.Should().BeTrue();
		result.IsApplicable.Should().BeFalse("there are no actions to hold to a version");
	}

	[Fact]
	public async Task Advisory_RewritesEveryUsageIncludingSubActions()
	{
		SeedCatalog(new() { ["github/codeql-action"] = "v4" });

		var result = await Evaluate(Ctx(CodeQl("v2")));

		var data = result.Advisory!.Data;
		data["remediation_type"].Should().Be("replace_regex_in_files");
		data["globs"].Should().BeOfType<string[]>()
			.Which.Should().Contain(".github/workflows/*.yml");

		var patterns = data["patterns"].Should().BeOfType<string[]>().Subject;
		var replacements = data["replacements"].Should().BeOfType<string[]>().Subject;
		patterns.Should().HaveSameCount(replacements);

		var rewritten = System.Text.RegularExpressions.Regex.Replace(
			"      - uses: github/codeql-action/init@v2\n      - uses: github/codeql-action/analyze@v2\n",
			patterns[0],
			replacements[0]);

		rewritten.Should().Contain("github/codeql-action/init@v4")
			.And.Contain("github/codeql-action/analyze@v4")
			.And.NotContain("@v2");
	}

	[Fact]
	public async Task Advisory_NamesOnlyTheActionsItWillMove()
	{
		SeedCatalog(new() { ["github/codeql-action"] = "v4", ["actions/cache"] = "v3" });

		var result = await Evaluate(Ctx(
			CodeQl("v2"),
			(".github/workflows/ci.yml", "jobs:\n  build:\n    steps:\n    - uses: actions/cache@v3\n")));

		result.Advisory!.Data["governed_actions"].Should().BeOfType<string[]>()
			.Which.Should().BeEquivalentTo(["github/codeql-action"],
				"actions/cache is already at the floor, so this failure will not move it");
	}

	[Fact]
	public void Governs_AnyActionNoOtherRuleClaims()
	{
		var rule = Rule().Should().BeAssignableTo<IGovernsDependency>().Subject;

		rule.Governs(new DependencyRef(DependencyEcosystem.GitHubActions, "github/codeql-action"))
			.Should().BeTrue();
		rule.Governs(new DependencyRef(DependencyEcosystem.GitHubActions, "actions/checkout"))
			.Should().BeFalse("CI-05 claims checkout, and two rules claiming one action is a coin toss");
		rule.Governs(new DependencyRef(DependencyEcosystem.NuGet, "Refit"))
			.Should().BeFalse("rewriting a workflow cannot move a package version");
	}
}
