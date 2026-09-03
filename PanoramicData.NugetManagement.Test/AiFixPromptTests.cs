using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="AiFixPrompt"/>: what a 27b model is actually told. The prompt is the feature —
/// the loop only decides how many chances it gets.
/// </summary>
public class AiFixPromptTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RuleResult Failing(
		string ruleId = "COM-01",
		string ruleName = "SECURITY.md exists",
		string? summary = "Add SECURITY.md with the standard security policy content",
		string? detail = "Long prose written for a frontier model, mentioning grace periods.",
		Dictionary<string, object>? data = null)
		=> new()
		{
			RuleId = ruleId,
			RuleName = ruleName,
			Category = AssessmentCategory.CommunityHealth,
			Severity = AssessmentSeverity.Error,
			Passed = false,
			Message = "SECURITY.md is missing.",
			Advisory = summary is null
				? null
				: new RuleAdvisory
				{
					Summary = summary,
					Detail = detail ?? string.Empty,
					Data = data ?? []
				}
		};

	private sealed class StubPlaybook : IRuleAiPlaybook
	{
		public string RuleId => "COM-01";

		public string Goal => "Create a SECURITY.md at the repository root.";

		public IReadOnlyList<string> Files => ["SECURITY.md"];

		public string ExpectedEndState => "SECURITY.md exists and names a contact for reports.";

		public string WorkedExample => "Write SECURITY.md containing '# Security Policy'.";
	}

	[Fact]
	public void SystemPrompt_TellsTheModelHowToBehave()
	{
		var system = AiFixPrompt.SystemPrompt;

		system.Should().Contain("smallest", "a small model left to its own judgement rewrites whole files");
		system.Should().Contain("finish", "it has to know how to end");
		system.Should().Contain("read", "reading before writing is the difference between an edit and a guess");
	}

	[Fact]
	public void SystemPrompt_IsShort()
		=> AiFixPrompt.SystemPrompt.Length.Should().BeLessThan(2_500,
			"it is spent on every turn of every attempt, and a small model's attention is the scarce resource");

	[Fact]
	public void WithAPlaybook_TheGoalAndEndStateLeadTheTask()
	{
		var task = AiFixPrompt.BuildTask(
			Failing(),
			"panoramicdata/Sample",
			new StubPlaybook());

		task.Should().Contain("Create a SECURITY.md at the repository root.");
		task.Should().Contain("SECURITY.md exists and names a contact");
		task.Should().Contain("# Security Policy", "the worked example is what a weak model copies");
		task.Should().Contain("SECURITY.md", "the files to touch have to be named");
	}

	[Fact]
	public void WithAPlaybook_TheProseAdvisoryIsLeftOut()
		=> AiFixPrompt.BuildTask(Failing(), "panoramicdata/Sample", new StubPlaybook())
			.Should().NotContain("grace periods",
				"Advisory.Detail is written for a frontier model, and for a small one it misleads");

	[Fact]
	public void WithNoPlaybook_TheAdvisoryIsUsedIncludingItsDetail()
	{
		var task = AiFixPrompt.BuildTask(Failing(), "panoramicdata/Sample", playbook: null);

		task.Should().Contain("Add SECURITY.md with the standard security policy content");
		task.Should().Contain("grace periods", "with no playbook the prose is all there is");
	}

	[Fact]
	public void TheRulesOwnFailureMessage_IsAlwaysIncluded()
		=> AiFixPrompt.BuildTask(Failing(), "panoramicdata/Sample", null)
			.Should().Contain("SECURITY.md is missing.",
				"it is the most specific statement of what is wrong that exists");

	[Fact]
	public void StructuredAdvisoryData_IsRenderedAsPlainKeyAndValue()
	{
		var task = AiFixPrompt.BuildTask(
			Failing(data: new Dictionary<string, object>
			{
				["expected_path"] = ".github/workflows/ci.yml",
				["missing_files"] = new[] { "a.txt", "b.txt" }
			}),
			"panoramicdata/Sample",
			null);

		task.Should().Contain("expected_path").And.Contain(".github/workflows/ci.yml");
		task.Should().Contain("a.txt").And.Contain("b.txt", "an array has to arrive readable, not as a type name");
		task.Should().NotContain("System.String[]");
	}

	/// <summary>
	/// The facts a file-grade rule emits: a list of dictionaries, each holding a list of its own.
	/// </summary>
	private static Dictionary<string, object> FileFacts() => new()
	{
		["files_below_minimum"] = 2,
		["files"] = new List<Dictionary<string, object?>>
		{
			new()
			{
				["path"] = "Publish.ps1",
				["grade_letter"] = "F",
				["issues"] = new List<Dictionary<string, object?>>
				{
					new() { ["line"] = 12L, ["pattern"] = "PSAvoidUsingWriteHost", ["message"] = "Avoid Write-Host." }
				}
			},
			new()
			{
				["path"] = "src/VtlParser.cs",
				["grade_letter"] = "B",
				["issues"] = new List<Dictionary<string, object?>>
				{
					new() { ["line"] = 88L, ["pattern"] = "SonarCSharp_S3776", ["message"] = "Reduce complexity." }
				}
			}
		}
	};

	[Fact]
	public void NestedAdvisoryData_ArrivesAsReadableLinesRatherThanTypeNames()
	{
		// This is what had the model guessing at issues it had in fact been sent: the whole nested
		// structure flattened to KeyValuePair.ToString and told it nothing.
		var task = AiFixPrompt.BuildTask(Failing(data: FileFacts()), "panoramicdata/Sample", null);

		task.Should().Contain("Publish.ps1");
		task.Should().Contain("PSAvoidUsingWriteHost", "the pattern is what says which nine things Codacy meant");
		task.Should().Contain("Avoid Write-Host.");
		task.Should().NotContain("System.Collections", "a type name is not a fact");
		task.Should().NotContain("[path,", "a flattened KeyValuePair is not a fact either");
	}

	/// <summary>
	/// A failure describing three files, exactly as CQ-06 emits one: a repository-wide message, a
	/// repository-wide advisory naming every file, and a per-file target for each.
	/// </summary>
	private static RuleResult FailingAcrossFiles() => new()
	{
		RuleId = "CQ-06",
		RuleName = "Codacy file grades",
		Category = AssessmentCategory.CodeQuality,
		Severity = AssessmentSeverity.Info,
		Passed = false,
		Message = "3 file(s) graded below A: Publish.ps1 (F), PanoramicData.Vtl/VtlParser.cs (B).",
		Advisory = new RuleAdvisory
		{
			Summary = "Improve 3 file(s) graded below A in panoramicdata/Sample.",
			Detail = "| Publish.ps1 | F | ... | PanoramicData.Vtl/VtlParser.cs | B | Reduce complexity. |",
			Data = FileFacts(),
			Targets =
			[
				new("Publish.ps1", "Publish.ps1 is graded F by Codacy.", "- line 12 — PSAvoidUsingWriteHost — Avoid Write-Host."),
				new("PanoramicData.Vtl/VtlParser.cs", "VtlParser.cs is graded B by Codacy.", "- line 88 — SonarCSharp_S3776 — Reduce complexity.")
			]
		}
	};

	[Fact]
	public void WithATargetFile_NothingInTheTaskMentionsAnyOtherFile()
	{
		// The first attempt at this narrowed only the Facts, and the test that was supposed to catch
		// that passed because its stub advisory never named the other file. The prompt still opened
		// with a rule message naming three files and a guidance section listing all three — so a
		// session told "change this one file and no other" was immediately shown two more.
		var task = AiFixPrompt.BuildTask(
			FailingAcrossFiles(),
			"panoramicdata/Sample",
			playbook: null,
			targetPath: "Publish.ps1");

		task.Should().Contain("Change this one file and no other: Publish.ps1");
		task.Should().Contain("Publish.ps1 is graded F by Codacy.", "the target's own summary replaces the rule's");
		task.Should().Contain("PSAvoidUsingWriteHost");

		task.Should().NotContain("VtlParser", "a small model told about two files will try to fix two files");
		task.Should().NotContain("Reduce complexity.");
		task.Should().NotContain("3 file(s)", "the repository-wide message describes work this session must not do");
		task.Should().NotContain("Improve 3", "nor does the repository-wide goal");
	}

	[Fact]
	public void WithNoTargetFile_TheRepositoryWideDescriptionIsUsed()
	{
		var task = AiFixPrompt.BuildTask(FailingAcrossFiles(), "panoramicdata/Sample", playbook: null);

		task.Should().Contain("3 file(s) graded below A");
		task.Should().Contain("VtlParser", "with no target, every file is in scope");
	}

	[Fact]
	public void WithATargetThatMatchesNothing_TheFactsAreLeftWhole()
	{
		// An empty Facts block would read as "there is nothing to do", which is worse than too much.
		var task = AiFixPrompt.BuildTask(
			Failing(data: FileFacts()),
			"panoramicdata/Sample",
			playbook: null,
			targetPath: "NotListed.cs");

		task.Should().Contain("Publish.ps1").And.Contain("VtlParser");
	}

	[Fact]
	public void WithNoTargetFile_TheTaskDoesNotClaimThereIsOne()
		=> AiFixPrompt.BuildTask(Failing(data: FileFacts()), "panoramicdata/Sample", null)
			.Should().NotContain("Change this one file");

	[Fact]
	public void TheTask_NamesTheRepositoryAndTheRule()
	{
		var task = AiFixPrompt.BuildTask(Failing(), "panoramicdata/Sample", null);

		task.Should().Contain("panoramicdata/Sample").And.Contain("COM-01");
	}

	[Fact]
	public void ARuleWithNoAdvisoryAtAll_StillProducesAUsableTask()
	{
		var task = AiFixPrompt.BuildTask(Failing(summary: null), "panoramicdata/Sample", null);

		task.Should().Contain("SECURITY.md is missing.");
		task.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public void PlaybookRegistry_FindsPlaybooksByRuleId()
	{
		var registry = new AiPlaybookRegistry([new StubPlaybook()]);

		registry.For("COM-01").Should().BeOfType<StubPlaybook>();
		registry.For("com-01").Should().NotBeNull("rule ids are compared without regard to case elsewhere");
		registry.For("CI-05").Should().BeNull();
	}

	[Fact]
	public void PlaybookRegistry_DiscoversWhatIsInTheAssembly()
		=> new AiPlaybookRegistry().RuleIds.Should().OnlyHaveUniqueItems(
			"two playbooks for one rule would make which one applies a matter of load order");

	[Fact]
	public void EveryDiscoveredPlaybook_NamesARuleThatExists()
	{
		var known = RuleRegistry.Rules.Select(r => r.RuleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

		new AiPlaybookRegistry().RuleIds.Should().OnlyContain(id => known.Contains(id),
			"a playbook for a rule that no longer exists is dead weight nobody will notice");
	}
}
