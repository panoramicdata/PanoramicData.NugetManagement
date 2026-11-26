using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>Guards the generated AGENTS.md against Agentlinter's findings as measured, and keeps earlier templates replaceable.</summary>
public class AgentsMdPriorityTests
{
	// Measured on a draft pull request against Codacy Agentlinter (2026-11-26): a priority statement plus three CRITICAL or
	// IMPORTANT items cleared "no priority signals"; the MUST NOT and SHOULD wording cleared that finding but was reported as
	// an undefined term and as an absolute rule without an escape hatch. Re-measure before relaxing these tests.
	private static readonly Regex _priorityMarker = new(@"\b(CRITICAL|IMPORTANT)\b", RegexOptions.None, TimeSpan.FromSeconds(2));
	private static readonly Regex _absoluteWord = new(@"\b(always|never|must|every|all|no exceptions)\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

	[Fact]
	public void PublicTemplate_ShouldCarryAPriorityStatementAndAtLeastThreeMarkedItems()
	{
		var lines = Standards.AgentsMdContent.Replace("\r\n", "\n").Split('\n');

		lines.Count(line => _priorityMarker.IsMatch(line)).Should().BeGreaterThanOrEqualTo(4);
		lines.Should().Contain("IMPORTANT: Apply these in order, most important first.");
	}

	[Fact]
	public void PublicTemplate_ShouldAvoidTheAbsoluteWordsAgentlinterReports()
	{
		_absoluteWord.IsMatch(Standards.AgentsMdContent).Should().BeFalse();
		Standards.AgentsMdContent.Should().NotContain("MUST").And.NotContain("SHOULD");
	}

	[Fact]
	public async Task AI02_ShouldReplaceAFileGeneratedFromAnEarlierTemplate()
	{
		const string earlier = """
			# Agent instructions

			## Identity

			You are a careful, security-conscious contributor to this repository.

			## Scope and boundaries

			- Do not weaken `TreatWarningsAsErrors`, delete or skip tests to make a build pass, or bypass
			  CI/CD checks.
			- Do not commit secrets, credentials, or API tokens.
			- Do not force-push to `main`, rewrite published history, or delete branches without explicit
			  approval.
			- Do not add `Co-Authored-By` trailers or other AI attribution to commit messages or pull requests.

			## Tools

			- Build and test with `dotnet build` / `dotnet test`.
			- Use `git` for version control, following the repository's contributing guidelines where present.

			## PanoramicData staff

			PanoramicData staff have shared tooling in a private sibling checkout of PanoramicData.Skills. Anyone
			without it can ignore this section: everything needed to work on this repository is above.

			""";

		foreach (var text in new[]
		{
			earlier,
			earlier.Replace("\n", "\r\n"),
			Standards.PreviousStandaloneAgentsMdContent,
			Standards.PreviousPriorityAgentsMdContent
		})
		{
			var context = CreateContext(new Dictionary<string, string> { ["AGENTS.md"] = text });

			var result = await GetRule("AI-02").EvaluateAsync(context, CancellationToken.None).ConfigureAwait(true);
			result.Passed.Should().BeFalse();
			result.Advisory!.Data["remediation_type"].Should().Be("replace_file_content");
			result.Advisory.Data["new_content"].Should().Be(Standards.AgentsMdContent);
		}
	}

	[Fact]
	public async Task AI02_ShouldNotReplaceAnEarlierTemplateThatAPersonHasEdited()
	{
		var edited = Standards.PreviousPriorityAgentsMdContent + "\nRun the Docker tests first.\n";
		var context = CreateContext(new Dictionary<string, string> { ["AGENTS.md"] = edited });

		var result = await GetRule("AI-02").EvaluateAsync(context, CancellationToken.None).ConfigureAwait(true);
		result.Advisory?.Data.ContainsKey("remediation_type").Should().NotBe(true);
	}

	[Fact]
	public async Task NewTemplate_ShouldPassAi02AndAi06AndLeakNothing()
	{
		var context = CreateContext(new Dictionary<string, string> { ["AGENTS.md"] = Standards.AgentsMdContent });

		(await GetRule("AI-02").EvaluateAsync(context, CancellationToken.None).ConfigureAwait(true)).Passed.Should().BeTrue();
		(await GetRule("AI-06").EvaluateAsync(context, CancellationToken.None).ConfigureAwait(true)).Passed.Should().BeTrue();
		Standards.FindPublicAgentsMdLeaks(Standards.AgentsMdContent).Should().BeEmpty();
		Standards.AgentsMdContent.Should().NotContain("—").And.NotContain("CONTRIBUTING.md");
	}

	[Fact]
	public async Task AI06_ShouldPass_WhenAgentsMdHasTheInstructionInItsEarlierWording()
	{
		var context = CreateContext(new Dictionary<string, string> { ["AGENTS.md"] = "# Agent instructions\n\n- " + Standards.LegacyNoAiAttributionInstruction + "\n" });

		(await GetRule("AI-06").EvaluateAsync(context, CancellationToken.None).ConfigureAwait(true)).Passed.Should().BeTrue();
	}

	private static IRule GetRule(string ruleId)
		=> RuleRegistry.Rules.Single(r => r.RuleId == ruleId);

	private static RepositoryContext CreateContext(Dictionary<string, string> files) => new()
	{
		FullName = "test-org/test-repo",
		Name = "test-repo",
		DefaultBranch = "main",
		CurrentBranch = "main",
		Options = new RepoOptions(),
		FilePaths = [.. files.Keys],
		FileContents = files,
		RepositoryConfig = null
	};
}
