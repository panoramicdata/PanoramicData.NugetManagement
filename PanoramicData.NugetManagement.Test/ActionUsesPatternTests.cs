using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="ActionUsesPattern"/>: the one definition of how a <c>uses:</c> line is moved
/// to a newer major, shared by CI-12 and by Dependabot adoption.
/// </summary>
public class ActionUsesPatternTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static string Rewrite(string line, string action, string target)
		=> Regex.Replace(
			line,
			ActionUsesPattern.Below(action, target),
			ActionUsesPattern.Replacement(target));

	[Fact]
	public void Below_MatchesAPinBelowTheTarget()
		=> Rewrite("      - uses: actions/checkout@v4", "actions/checkout", "v5")
			.Should().Be("      - uses: actions/checkout@v5");

	[Fact]
	public void Below_AcceptsATargetWrittenWithoutTheVPrefix()
		=> Rewrite("      - uses: actions/checkout@v4", "actions/checkout", "5")
			.Should().Be(
				"      - uses: actions/checkout@v5",
				"a Dependabot body writes an action version as '4 to 5', with no v");

	[Fact]
	public void Below_LeavesAPinAtTheTargetAlone()
		=> Rewrite("      - uses: actions/checkout@v5", "actions/checkout", "v5")
			.Should().Be("      - uses: actions/checkout@v5");

	[Fact]
	public void Below_LeavesAPinAboveTheTargetAlone()
		=> Rewrite("      - uses: actions/checkout@v6", "actions/checkout", "v5")
			.Should().Be(
				"      - uses: actions/checkout@v6",
				"a repository ahead of the target must never be moved backwards");

	[Fact]
	public void Below_RewritesASubActionToo()
		=> Rewrite("      - uses: github/codeql-action/init@v2", "github/codeql-action", "v4")
			.Should().Be(
				"      - uses: github/codeql-action/init@v4",
				"a sub-action has no version of its own — the repository's version is what is pinned");

	[Fact]
	public void Below_RewritesAPatchQualifiedPinToTheBareMajor()
		=> Rewrite("      - uses: actions/checkout@v3.1.2", "actions/checkout", "v5")
			.Should().Be("      - uses: actions/checkout@v5");

	[Fact]
	public void Below_LeavesACommitShaPinAlone()
		=> Rewrite(
				"      - uses: actions/checkout@8f4b7f84864484a7bf31766abe9204da3cbe65b3",
				"actions/checkout",
				"v5")
			.Should().Be(
				"      - uses: actions/checkout@8f4b7f84864484a7bf31766abe9204da3cbe65b3",
				"a SHA pin is a deliberate choice and is not a version this can reason about");

	[Fact]
	public void Below_DoesNotMatchAHigherMajorSharingALeadingDigit()
		=> Rewrite("      - uses: actions/checkout@v12", "actions/checkout", "v2")
			.Should().Be(
				"      - uses: actions/checkout@v12",
				"v1 must not match the start of v12, or a repository well ahead is dragged back");

	[Fact]
	public void Below_LeavesADifferentActionAlone()
		=> Rewrite("      - uses: actions/setup-dotnet@v1", "actions/checkout", "v5")
			.Should().Be("      - uses: actions/setup-dotnet@v1");

	[Fact]
	public void WorkflowGlobs_CoverBothYamlExtensions()
		=> ActionUsesPattern.WorkflowGlobs.Should().BeEquivalentTo(
			[".github/workflows/*.yml", ".github/workflows/*.yaml"],
			"a workflow written as .yaml is still a workflow");
}
