using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="ActionUsageScanner"/>: it reads the version of every action a repository's
/// workflows use, and reports "unreadable" rather than guessing when a usage is SHA-pinned.
/// </summary>
public class ActionUsageScannerTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RepositoryContext Ctx(params (string Path, string Content)[] files) => new()
	{
		FullName = "panoramicdata/Sample",
		Name = "Sample",
		DefaultBranch = "main",
		CurrentBranch = "main",
		Options = new RepoOptions(),
		FilePaths = [.. files.Select(f => f.Path)],
		FileContents = files.ToDictionary(f => f.Path, f => f.Content, StringComparer.OrdinalIgnoreCase)
	};

	[Fact]
	public void Scan_ReadsUsagesFromEveryWorkflowFile()
	{
		var usages = ActionUsageScanner.Scan(Ctx(
			(".github/workflows/ci.yml", "steps:\n    - uses: actions/checkout@v7\n"),
			(".github/workflows/codeql.yml", "steps:\n    - uses: github/codeql-action@v2\n")));

		usages.Select(u => u.Action)
			.Should().BeEquivalentTo(["actions/checkout", "github/codeql-action"]);
	}

	[Fact]
	public void Scan_ReadsTheSameActionUsedMoreThanOnce()
	{
		var usages = ActionUsageScanner.Scan(Ctx(
			(".github/workflows/ci.yml",
				"steps:\n    - uses: actions/checkout@v7\n    - uses: actions/checkout@v3\n")));

		usages.Should().HaveCount(2, "each usage is reported separately; the lowest one decides satisfaction");
		usages.Select(u => u.MajorVersion).Should().BeEquivalentTo([7, 3]);
	}

	[Theory]
	[InlineData("v3", 3)]
	[InlineData("v3.1.2", 3)]
	[InlineData("3", 3)]
	[InlineData("v10", 10)]
	public void Scan_ReadsTheMajorVersionFromTheSpec(string spec, int expected)
	{
		var usages = ActionUsageScanner.Scan(Ctx(
			(".github/workflows/ci.yml", $"steps:\n    - uses: actions/checkout@{spec}\n")));

		usages.Should().ContainSingle().Which.MajorVersion.Should().Be(expected);
	}

	[Fact]
	public void Scan_ShaPinnedUsage_ReportsNoReadableMajorVersion()
	{
		var usages = ActionUsageScanner.Scan(Ctx(
			(".github/workflows/ci.yml",
				"steps:\n    - uses: actions/checkout@8f4b7f84864484a7bf31766abe9204da3cbe65b3 # v4\n")));

		usages.Should().ContainSingle().Which.MajorVersion.Should().BeNull(
			"a commit SHA is not a version, and guessing one could close a pull request wrongly");
	}

	[Fact]
	public void Scan_IgnoresFilesOutsideTheWorkflowsFolder()
		=> ActionUsageScanner
			.Scan(Ctx(("README.md", "example:\n    - uses: actions/checkout@v1\n")))
			.Should().BeEmpty();

	[Fact]
	public void Scan_IgnoresLocalAndDockerSteps()
		=> ActionUsageScanner
			.Scan(Ctx((".github/workflows/ci.yml",
				"steps:\n    - uses: ./.github/actions/setup\n    - uses: docker://alpine:3.19\n")))
			.Should().BeEmpty("neither names a versioned GitHub Action that Dependabot would bump");

	[Fact]
	public void LowestMajorOf_TakesTheWeakestUsage()
	{
		var usages = ActionUsageScanner.Scan(Ctx(
			(".github/workflows/ci.yml", "steps:\n    - uses: actions/checkout@v7\n"),
			(".github/workflows/codeql.yml", "steps:\n    - uses: actions/checkout@v3\n")));

		ActionUsageScanner.LowestMajorOf(usages, "actions/checkout").Should().Be(3,
			"one workflow left behind means the pull request still has work to do");
	}

	[Fact]
	public void LowestMajorOf_AnyUnreadableUsage_IsUnreadableOverall()
	{
		var usages = ActionUsageScanner.Scan(Ctx(
			(".github/workflows/ci.yml", "steps:\n    - uses: actions/checkout@v7\n"),
			(".github/workflows/codeql.yml", "steps:\n    - uses: actions/checkout@abc1234 # v4\n")));

		ActionUsageScanner.LowestMajorOf(usages, "actions/checkout").Should().BeNull(
			"a version we could not read must never be treated as satisfied");
	}

	[Fact]
	public void LowestMajorOf_ActionNotUsedAtAll_IsUnreadable()
		=> ActionUsageScanner
			.LowestMajorOf([], "actions/checkout")
			.Should().BeNull("an action the repository does not use cannot be shown to satisfy anything");
}
