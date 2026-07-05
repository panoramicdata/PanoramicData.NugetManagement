using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that the CI action-version rules are version-aware: a repository at or above the floor
/// passes (including when it is AHEAD of the recommended version), and only genuinely-behind
/// repositories fail.
/// </summary>
public class CiVersionRuleTests(ITestOutputHelper output) : TestWithOutput(output)
{
	// Use an in-memory catalog (null path) so the version-aware rules never write to the committed
	// action-versions.json when these tests exercise versions above the floor.
	static CiVersionRuleTests() => ActionVersionCatalog.Default = new ActionVersionCatalog(null);

	private const string _ciPath = ".github/workflows/ci.yml";

	private static RepositoryContext Ctx(string ciYml) => new()
	{
		FullName = "panoramicdata/Sample",
		Name = "Sample",
		DefaultBranch = "main",
		CurrentBranch = "main",
		Options = new RepoOptions(),
		FilePaths = [_ciPath],
		FileContents = new() { [_ciPath] = ciYml }
	};

	private static IRule Rule(string id) => RuleRegistry.Rules.Single(r => r.RuleId == id);

	private static string Checkout(string version) => $"jobs:\n  build:\n    steps:\n    - uses: actions/checkout@{version}\n";

	private static string SetupDotnet(string version) =>
		$"jobs:\n  build:\n    steps:\n    - uses: actions/setup-dotnet@{version}\n      with:\n        dotnet-version: 10.0.x\n";

	[Theory]
	[InlineData("v6")] // exactly the floor
	[InlineData("v7")] // AHEAD of the floor — must still pass, not be flagged as wrong
	[InlineData("v10")]
	public async Task CI05_Passes_AtOrAboveFloor(string version)
	{
		var result = await Rule("CI-05").EvaluateAsync(Ctx(Checkout(version)), TestContext.Current.CancellationToken);
		result.Passed.Should().BeTrue($"checkout@{version} is at or above the floor");
	}

	[Theory]
	[InlineData("v3")]
	[InlineData("v5")]
	public async Task CI05_Fails_BelowFloor(string version)
	{
		var result = await Rule("CI-05").EvaluateAsync(Ctx(Checkout(version)), TestContext.Current.CancellationToken);
		result.Passed.Should().BeFalse($"checkout@{version} is below the floor");
		result.Advisory.Should().NotBeNull();
	}

	[Theory]
	[InlineData("v5")]
	[InlineData("v6")] // ahead
	public async Task CI06_Passes_AtOrAboveFloor(string version)
	{
		var result = await Rule("CI-06").EvaluateAsync(Ctx(SetupDotnet(version)), TestContext.Current.CancellationToken);
		result.Passed.Should().BeTrue($"setup-dotnet@{version} + 10.0.x is at or above the floor");
	}

	[Fact]
	public async Task CI06_Fails_BelowFloor()
	{
		var result = await Rule("CI-06").EvaluateAsync(Ctx(SetupDotnet("v4")), TestContext.Current.CancellationToken);
		result.Passed.Should().BeFalse();
	}

	[Theory]
	[InlineData("actions/checkout", "uses: actions/checkout@v6\nuses: actions/checkout@v4", 6)] // highest wins
	[InlineData("actions/setup-dotnet", "actions/setup-dotnet@v5", 5)]
	[InlineData("actions/checkout", "no actions here", null)]
	public void GetHighestUsedMajor_ReturnsMax(string action, string content, int? expected)
		=> GitHubActionVersion.GetHighestUsedMajor(content, action).Should().Be(expected);

	[Fact]
	public void UsesAtLeast_TrueWhenAhead()
		=> GitHubActionVersion.UsesAtLeast("actions/checkout@v9", "actions/checkout", "v6", out _).Should().BeTrue();
}
