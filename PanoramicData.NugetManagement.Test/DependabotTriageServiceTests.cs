using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="DependabotTriageService"/>: the verdict it reaches on each open Dependabot
/// pull request, given what the repository declares and which rules are failing.
/// </summary>
public class DependabotTriageServiceTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _packagesProps = "Directory.Packages.props";
	private const string _ciPath = ".github/workflows/ci.yml";
	private const string _codeQlPath = ".github/workflows/codeql.yml";

	private static RepositoryIssue PullRequest(int number, string title, string author = "dependabot[bot]")
		=> new()
		{
			Number = number,
			Title = title,
			IsPullRequest = true,
			HtmlUrl = $"https://github.com/panoramicdata/Athonet.Api/pull/{number}",
			AuthorLogin = author,
			CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
		};

	private static RepositoryContext Ctx(params (string Path, string Content)[] files) => new()
	{
		FullName = "panoramicdata/Athonet.Api",
		Name = "Athonet.Api",
		DefaultBranch = "main",
		CurrentBranch = "main",
		Options = new RepoOptions(),
		FilePaths = [.. files.Select(f => f.Path)],
		FileContents = files.ToDictionary(f => f.Path, f => f.Content, StringComparer.OrdinalIgnoreCase)
	};

	private static (string, string) Packages(string packageId, string version)
		=> (_packagesProps,
			$"""<Project><ItemGroup><PackageVersion Include="{packageId}" Version="{version}" /></ItemGroup></Project>""");

	private static (string, string) Workflow(string path, params string[] uses)
		=> (path, "jobs:\n  build:\n    steps:\n" + string.Concat(uses.Select(u => $"    - uses: {u}\n")));

	private static RuleResult Failing(string ruleId) => new()
	{
		RuleId = ruleId,
		RuleName = ruleId,
		Category = AssessmentCategory.CiCd,
		Severity = AssessmentSeverity.Error,
		Passed = false,
		Message = "failing, for this test"
	};

	/// <summary>
	/// A failing result from a rule that governs a whole ecosystem but will only move the dependencies
	/// it names — as CI-12 does, claiming every action no other rule owns while fixing only the ones
	/// actually behind.
	/// </summary>
	private static RuleResult FailingNaming(string ruleId, params string[] actions) => new()
	{
		RuleId = ruleId,
		RuleName = ruleId,
		Category = AssessmentCategory.CiCd,
		Severity = AssessmentSeverity.Error,
		Passed = false,
		Message = "failing, for this test",
		Advisory = new RuleAdvisory
		{
			Summary = "Update the actions this names",
			Detail = "Update the actions this names.",
			Data = new() { ["governed_actions"] = actions }
		}
	};

	/// <summary>
	/// A failing result from a package rule. These claim every NuGet package and move the ones they
	/// named, so the names are what a test has to supply for a failure to cover anything.
	/// </summary>
	private static RuleResult FailingNamingPackages(string ruleId, params string[] packages) => new()
	{
		RuleId = ruleId,
		RuleName = ruleId,
		Category = AssessmentCategory.NuGetHygiene,
		Severity = AssessmentSeverity.Error,
		Passed = false,
		Message = "failing, for this test",
		Advisory = new RuleAdvisory
		{
			Summary = "Update the packages this names",
			Detail = "Update the packages this names.",
			Data = new() { [NuGetPackageUpdateRuleBase.GovernedPackagesKey] = packages }
		}
	};

	/// <summary>Triages one pull request, with every governing rule remediable unless stated.</summary>
	private static DependabotTriage TriageOne(
		RepositoryIssue issue,
		RepositoryContext context,
		IReadOnlyList<RuleResult>? ruleResults = null,
		Func<string, bool>? canRemediate = null)
		=> new DependabotTriageService()
			.Triage([issue], context, ruleResults ?? [], canRemediate ?? (_ => true))
			.Should().ContainSingle().Subject;

	[Fact]
	public void GroupedPullRequest_IsUnrecognisedAndCarriesNoProposal()
	{
		var triage = TriageOne(PullRequest(1, "Bump the nuget group with 3 updates"), Ctx());

		triage.Verdict.Should().Be(DependabotVerdict.Unrecognised);
		triage.Proposal.Should().BeNull();
	}

	[Fact]
	public void PackageDeclaredAtTheTargetVersion_IsAlreadySatisfied()
		=> TriageOne(
				PullRequest(1, "Bump refit from 6.3.2 to 7.2.22"),
				Ctx(Packages("refit", "7.2.22")))
			.Verdict.Should().Be(DependabotVerdict.AlreadySatisfied);

	[Fact]
	public void PackageDeclaredAboveTheTargetVersion_IsAlreadySatisfied()
		=> TriageOne(
				PullRequest(1, "Bump refit from 6.3.2 to 7.2.22"),
				Ctx(Packages("refit", "8.0.0")))
			.Verdict.Should().Be(DependabotVerdict.AlreadySatisfied);

	[Fact]
	public void PackageDeclaredBelowTarget_WithAFailingGoverningRule_IsCovered()
	{
		var triage = TriageOne(
			PullRequest(1, "Bump refit from 6.3.2 to 7.2.22"),
			Ctx(Packages("refit", "6.3.2")),
			[FailingNamingPackages("PKG-07", "refit")]);

		triage.Verdict.Should().Be(DependabotVerdict.ValidCovered);
		triage.CoveringRuleId.Should().Be("PKG-07");
	}

	[Fact]
	public void PackageDeclaredBelowTarget_WithNoFailingRule_IsUncovered()
		=> TriageOne(
				PullRequest(1, "Bump refit from 6.3.2 to 7.2.22"),
				Ctx(Packages("refit", "6.3.2")),
				ruleResults: [])
			.Verdict.Should().Be(DependabotVerdict.ValidUncovered,
				"a rule that is not failing will not be remediated, so it cannot cover this");

	[Fact]
	public void PackageDeclaredBelowTarget_WithNoFailingRule_IsNotARuleSetGap()
		=> TriageOne(
				PullRequest(1, "Bump refit from 6.3.2 to 7.2.22"),
				Ctx(Packages("refit", "6.3.2")),
				ruleResults: [])
			.IsRuleSetGap.Should().BeFalse(
				"the package rules govern every NuGet package and can see this one, so a pass today is "
				+ "a rule with nothing to say rather than a gap for somebody to fill");

	[Fact]
	public void APackageRuleFailingAboutAnotherPackage_DoesNotCoverThisOne()
	{
		var triage = TriageOne(
			PullRequest(1, "Bump refit from 6.3.2 to 7.2.22"),
			Ctx(Packages("refit", "6.3.2")),
			[FailingNamingPackages("PKG-07", "Newtonsoft.Json")]);

		triage.Verdict.Should().Be(DependabotVerdict.ValidUncovered,
			"the failure will move Newtonsoft.Json and nothing else, so reporting this as covered "
			+ "would leave it waiting for a fix that never touches it");
		triage.CoveringRuleId.Should().BeNull();
	}

	[Fact]
	public void ADependencyDeclaredWhereNoScannerReads_IsARuleSetGap()
	{
		// nbgv lives in .config/dotnet-tools.json, which PackageReferenceScanner does not read. The
		// package rules claim it all the same, so "governed" alone would hide it forever.
		var triage = TriageOne(
			PullRequest(1, "Bump nbgv from 3.9.50 to 3.10.94"),
			Ctx(Packages("refit", "7.2.22")));

		triage.Verdict.Should().Be(DependabotVerdict.ValidUncovered);
		triage.IsRuleSetGap.Should().BeTrue(
			"no failure of the rule that claims it can ever name a package the scanner never sees");
		triage.Reason.Should().Contain("never reads where it is declared");
	}

	[Fact]
	public void PackageDeclaredInTwoPlaces_OneBehind_IsNotSatisfied()
	{
		var context = Ctx(
			Packages("refit", "7.2.22"),
			("src/Sample.csproj",
				"""<Project><ItemGroup><PackageReference Include="refit" Version="6.3.2" /></ItemGroup></Project>"""));

		TriageOne(
				PullRequest(1, "Bump refit from 6.3.2 to 7.2.22"),
				context,
				[FailingNamingPackages("PKG-07", "refit")])
			.Verdict.Should().Be(DependabotVerdict.ValidCovered,
				"one declaration left behind means the bump still has work to do");
	}

	[Fact]
	public void PackageNotDeclaredAnywhere_IsNotSatisfied()
		=> TriageOne(PullRequest(1, "Bump refit from 6.3.2 to 7.2.22"), Ctx())
			.Verdict.Should().NotBe(DependabotVerdict.AlreadySatisfied,
				"nothing declared means nothing proven, and an unprovable claim must not close a pull request");

	[Fact]
	public void ActionAtTheTargetMajorInEveryWorkflow_IsAlreadySatisfied()
		=> TriageOne(
				PullRequest(3, "Bump actions/checkout from 3 to 6"),
				Ctx(Workflow(_ciPath, "actions/checkout@v7"), Workflow(_codeQlPath, "actions/checkout@v6")))
			.Verdict.Should().Be(DependabotVerdict.AlreadySatisfied);

	[Fact]
	public void ActionBehindTheTargetInOneWorkflow_IsStillValid()
		=> TriageOne(
				PullRequest(3, "Bump actions/checkout from 3 to 6"),
				Ctx(Workflow(_ciPath, "actions/checkout@v7"), Workflow(_codeQlPath, "actions/checkout@v3")),
				[Failing("CI-05")])
			.Verdict.Should().Be(DependabotVerdict.ValidCovered);

	[Fact]
	public void ShaPinnedAction_IsNeverAlreadySatisfied()
		=> TriageOne(
				PullRequest(3, "Bump actions/checkout from 3 to 6"),
				Ctx(Workflow(_ciPath, "actions/checkout@8f4b7f84864484a7bf31766abe9204da3cbe65b3 # v7")),
				[Failing("CI-05")])
			.Verdict.Should().NotBe(DependabotVerdict.AlreadySatisfied,
				"a version we could not read must never justify closing a pull request");

	[Fact]
	public void ActionNoRuleGoverns_IsUncovered()
		=> TriageOne(
				PullRequest(5, "Bump github/codeql-action from 2 to 4"),
				Ctx(Workflow(_codeQlPath, "github/codeql-action@v2")),
				[Failing("CI-05"), Failing("COM-04")])
			.Verdict.Should().Be(DependabotVerdict.ValidUncovered,
				"COM-04 only checks the workflow exists, and CI-05 governs a different action");

	[Fact]
	public void EcosystemWideRule_CoversTheActionItsFailureNames()
	{
		var triage = TriageOne(
			PullRequest(5, "Bump github/codeql-action from 2 to 4"),
			Ctx(Workflow(_codeQlPath, "github/codeql-action/init@v2")),
			[FailingNaming("CI-12", "github/codeql-action")]);

		triage.Verdict.Should().Be(DependabotVerdict.ValidCovered);
		triage.CoveringRuleId.Should().Be("CI-12");
	}

	[Fact]
	public void EcosystemWideRule_DoesNotCoverAnActionItsFailureDoesNotName()
		=> TriageOne(
				PullRequest(6, "Bump actions/cache from 3 to 4"),
				Ctx(Workflow(_ciPath, "actions/cache@v3", "github/codeql-action/init@v2")),
				[FailingNaming("CI-12", "github/codeql-action")])
			.Verdict.Should().Be(DependabotVerdict.ValidUncovered,
				"the fix will rewrite codeql-action and nothing else, so calling this covered would "
				+ "leave the pull request open forever waiting on a fix that never touches it");

	[Fact]
	public void GoverningRuleFailsButHasNoRemediation_IsUncovered()
		=> TriageOne(
				PullRequest(3, "Bump actions/checkout from 3 to 6"),
				Ctx(Workflow(_ciPath, "actions/checkout@v3")),
				[Failing("CI-05")],
				canRemediate: _ => false)
			.Verdict.Should().Be(DependabotVerdict.ValidUncovered);

	[Fact]
	public void HumanPullRequest_IsUnrecognised()
		=> TriageOne(
				PullRequest(9, "Bump refit from 6.3.2 to 7.2.22", author: "davidbond"),
				Ctx(Packages("refit", "7.2.22")))
			.Verdict.Should().Be(DependabotVerdict.Unrecognised,
				"a human's pull request is never closed by triage, satisfied or not");

	[Fact]
	public void EveryVerdictCarriesAReason()
		=> new DependabotTriageService()
			.Triage(
				[
					PullRequest(1, "Bump the nuget group with 3 updates"),
					PullRequest(2, "Bump refit from 6.3.2 to 7.2.22"),
					PullRequest(3, "Bump actions/checkout from 3 to 6")
				],
				Ctx(Packages("refit", "7.2.22"), Workflow(_ciPath, "actions/checkout@v3")),
				[Failing("CI-05")],
				_ => true)
			.Should().OnlyContain(t => !string.IsNullOrWhiteSpace(t.Reason),
				"the reason is written into the closing comment and the work item log");
}
