using Microsoft.Extensions.Time.Testing;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the <see cref="DependabotVerdict.Adoptable"/> verdict: when a pull request nothing is
/// failing for becomes old enough to act on anyway, and exactly what would then be written.
/// </summary>
public class DependabotAdoptionPlanTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _packagesProps = "Directory.Packages.props";
	private const string _ciPath = ".github/workflows/ci.yml";

	private static readonly DateTimeOffset _raised = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private static RepositoryIssue PullRequest(string body) => new()
	{
		Number = 6,
		Title = "Bump the group",
		IsPullRequest = true,
		HtmlUrl = "https://github.com/panoramicdata/Highlight.Api/pull/6",
		AuthorLogin = "dependabot[bot]",
		CreatedAtUtc = _raised,
		Body = body
	};

	private static string Moved(string name, string from, string to)
		=> $"Updated [{name}](https://github.com/example/{name}) from {from} to {to}.";

	private static RepositoryContext Ctx(params (string Path, string Content)[] files) => new()
	{
		FullName = "panoramicdata/Highlight.Api",
		Name = "Highlight.Api",
		DefaultBranch = "main",
		CurrentBranch = "main",
		Options = new RepoOptions(),
		FilePaths = [.. files.Select(f => f.Path)],
		FileContents = files.ToDictionary(f => f.Path, f => f.Content, StringComparer.OrdinalIgnoreCase)
	};

	private static (string, string) Packages(params (string Id, string Version)[] packages)
		=> (_packagesProps,
			"<Project><ItemGroup>"
				+ string.Concat(packages.Select(p =>
					$"""<PackageVersion Include="{p.Id}" Version="{p.Version}" />"""))
				+ "</ItemGroup></Project>");

	private static (string, string) Workflow(params string[] uses)
		=> (_ciPath, "jobs:\n  build:\n    steps:\n" + string.Concat(uses.Select(u => $"    - uses: {u}\n")));

	/// <summary>
	/// Triage as of a given age for the pull request, over the real package and action rules — which
	/// govern their whole ecosystems and, being passed no failing results, are failing for nothing.
	/// </summary>
	private static DependabotTriage JudgeAt(RepositoryIssue issue, RepositoryContext context, TimeSpan age)
		=> new DependabotTriageService(
				[new NuGetMajorLevelUpdatesRule(), new CiActionVersionFloorRule()],
				new FakeTimeProvider(issue.CreatedAtUtc + age))
			.Triage([issue], context, [], _ => true)[0];

	[Fact]
	public void PastTheThreshold_IsAdoptable()
		=> JudgeAt(
				PullRequest(Moved("coverlet.collector", "8.0.1", "10.0.0")),
				Ctx(Packages(("coverlet.collector", "8.0.1"))),
				DependabotTriageService.AdoptAfter + TimeSpan.FromDays(1))
			.Verdict.Should().Be(
				DependabotVerdict.Adoptable,
				"nothing is failing for it, but it has been open long enough that no grace period is "
					+ "still protecting anything");

	[Fact]
	public void OneDayShortOfTheThreshold_IsNotAdoptable()
		=> JudgeAt(
				PullRequest(Moved("coverlet.collector", "8.0.1", "10.0.0")),
				Ctx(Packages(("coverlet.collector", "8.0.1"))),
				DependabotTriageService.AdoptAfter - TimeSpan.FromDays(1))
			.Verdict.Should().Be(
				DependabotVerdict.ValidUncovered,
				"inside the threshold the grace periods are still doing their job, and adoption must not "
					+ "pre-empt them");

	[Fact]
	public void ExactlyAtTheThreshold_IsAdoptable()
		=> JudgeAt(
				PullRequest(Moved("coverlet.collector", "8.0.1", "10.0.0")),
				Ctx(Packages(("coverlet.collector", "8.0.1"))),
				DependabotTriageService.AdoptAfter)
			.Verdict.Should().Be(
				DependabotVerdict.Adoptable,
				"the threshold is inclusive, so a pull request exactly that old is acted on rather than "
					+ "waiting one more pass");

	[Fact]
	public void Adoptable_PlansThePackageRewrite()
	{
		var plan = JudgeAt(
			PullRequest(Moved("coverlet.collector", "8.0.1", "10.0.0")),
			Ctx(Packages(("coverlet.collector", "8.0.1"))),
			DependabotTriageService.AdoptAfter + TimeSpan.FromDays(1)).Adoption;

		plan.Should().NotBeNull();
		plan!.PackageUpdates.Should().BeEquivalentTo(
			[$"{_packagesProps}|coverlet.collector|PackageVersionAttribute|8.0.1|10.0.0"],
			"update_package_versions parses file|package|kind|from|to, and the target is what the pull "
				+ "request proposed rather than any newer version the cache knows about");
		plan.ActionPatterns.Should().BeEmpty();
	}

	[Fact]
	public void Adoptable_PlansOneRewritePerDeclarationSite()
	{
		var plan = JudgeAt(
			PullRequest(Moved("Serilog", "3.0.0", "4.0.0")),
			Ctx(
				Packages(("Serilog", "3.0.0")),
				("src/One/One.csproj",
					"""<Project><ItemGroup><PackageReference Include="Serilog" Version="3.0.0" /></ItemGroup></Project>""")),
			DependabotTriageService.AdoptAfter + TimeSpan.FromDays(1)).Adoption;

		plan.Should().NotBeNull();
		plan!.PackageUpdates.Should().HaveCount(
			2,
			"a package pinned in two files has to move in both, or the next pass finds it still "
				+ "unsatisfied");
	}

	[Fact]
	public void AdoptableAction_PlansTheWorkflowRewrite()
	{
		var plan = JudgeAt(
			PullRequest(Moved("actions/checkout", "4", "5")),
			Ctx(Workflow("actions/checkout@v4")),
			DependabotTriageService.AdoptAfter + TimeSpan.FromDays(1)).Adoption;

		plan.Should().NotBeNull();
		plan!.ActionPatterns.Should().BeEquivalentTo([ActionUsesPattern.Below("actions/checkout", "5")]);
		plan.ActionReplacements.Should().BeEquivalentTo([ActionUsesPattern.Replacement("5")]);
		plan.PackageUpdates.Should().BeEmpty();
	}

	[Fact]
	public void GroupWithOneUndeclaredBump_IsNotAdoptable()
		=> JudgeAt(
				PullRequest(
					Moved("coverlet.collector", "8.0.1", "10.0.0")
					+ "\n"
					+ Moved("Serilog", "3.0.0", "4.0.0")),
				Ctx(Packages(("coverlet.collector", "8.0.1"))),
				DependabotTriageService.AdoptAfter + TimeSpan.FromDays(1))
			.Verdict.Should().NotBe(
				DependabotVerdict.Adoptable,
				"Serilog is declared nowhere the scanner reads, so adopting part of the group and closing "
					+ "it would silently drop the rest");

	[Fact]
	public void GroupWhereEveryBumpIsWritable_IsAdoptableAndPlansAllOfThem()
	{
		var plan = JudgeAt(
			PullRequest(
				Moved("coverlet.collector", "8.0.1", "10.0.0")
				+ "\n"
				+ Moved("actions/checkout", "4", "5")),
			Ctx(Packages(("coverlet.collector", "8.0.1")), Workflow("actions/checkout@v4")),
			DependabotTriageService.AdoptAfter + TimeSpan.FromDays(1)).Adoption;

		plan.Should().NotBeNull("both halves of the group can be written");
		plan!.PackageUpdates.Should().HaveCount(1);
		plan.ActionPatterns.Should().HaveCount(1);
	}

	[Fact]
	public void AlreadySatisfiedPastTheThreshold_IsStillAlreadySatisfied()
		=> JudgeAt(
				PullRequest(Moved("coverlet.collector", "8.0.1", "10.0.0")),
				Ctx(Packages(("coverlet.collector", "10.0.0"))),
				DependabotTriageService.AdoptAfter + TimeSpan.FromDays(1))
			.Verdict.Should().Be(
				DependabotVerdict.AlreadySatisfied,
				"age never turns a redundant pull request into one worth writing");

	[Fact]
	public void CoveredPastTheThreshold_IsStillCovered()
	{
		var failing = new RuleResult
		{
			RuleId = "PKG-07",
			RuleName = "PKG-07",
			Category = AssessmentCategory.NuGetHygiene,
			Severity = AssessmentSeverity.Error,
			Passed = false,
			Message = "failing, for this test",
			Advisory = new RuleAdvisory
			{
				Summary = "Update the packages this names",
				Detail = "Update the packages this names.",
				Data = new()
				{
					[NuGetPackageUpdateRuleBase.GovernedPackagesKey] = new[] { "coverlet.collector" }
				}
			}
		};

		var issue = PullRequest(Moved("coverlet.collector", "8.0.1", "10.0.0"));

		new DependabotTriageService(
				[new NuGetMajorLevelUpdatesRule()],
				new FakeTimeProvider(_raised + DependabotTriageService.AdoptAfter + TimeSpan.FromDays(1)))
			.Triage([issue], Ctx(Packages(("coverlet.collector", "8.0.1"))), [failing], _ => true)[0]
			.Verdict.Should().Be(
				DependabotVerdict.ValidCovered,
				"a failing rule is already going to move it, and letting the rule do it keeps one "
					+ "mechanism responsible and lets the estate floors pick the version");
	}

	[Fact]
	public void UngovernedButDeclaredPastTheThreshold_IsAdoptedRatherThanRaisedAsAGap()
	{
		// No rule governs a NuGet package when no package rule is in the set, so nothing would ever move
		// this on its own — the definition of a rule-set gap.
		var triage = new DependabotTriageService(
				[new CiActionVersionFloorRule()],
				new FakeTimeProvider(_raised + DependabotTriageService.AdoptAfter + TimeSpan.FromDays(1)))
			.Triage(
				[PullRequest(Moved("coverlet.collector", "8.0.1", "10.0.0"))],
				Ctx(Packages(("coverlet.collector", "8.0.1"))),
				[],
				_ => true)[0];

		triage.Verdict.Should().Be(
			DependabotVerdict.Adoptable,
			"adoption is checked before the gap, and something we can write right now is not something "
				+ "nothing here can ever move");
		triage.IsRuleSetGap.Should().BeFalse();
		triage.GapBumps.Should().BeEmpty(
			"raising an issue saying nothing can ever move this, in the same pass that moves it, would "
				+ "be raising an issue against a lie");
	}

	[Fact]
	public void GovernedButUndeclaredPastTheThreshold_IsStillAGap()
	{
		// The package rules claim every NuGet package, but this one is declared nowhere the scanner
		// reads — the nbgv case. Nothing can write it, so age cannot rescue it.
		var triage = new DependabotTriageService(
				[new NuGetMajorLevelUpdatesRule()],
				new FakeTimeProvider(_raised + DependabotTriageService.AdoptAfter + TimeSpan.FromDays(1)))
			.Triage(
				[PullRequest(Moved("nbgv", "3.6.133", "3.7.115"))],
				Ctx(Packages(("coverlet.collector", "8.0.1"))),
				[],
				_ => true)[0];

		triage.Verdict.Should().Be(DependabotVerdict.ValidUncovered);
		triage.IsRuleSetGap.Should().BeTrue(
			"a dependency no scanner reads cannot be written by adoption either, so it stays somebody's "
				+ "work however long the pull request waits");
		triage.GapBumps.Select(b => b.Dependency.Name).Should().BeEquivalentTo(["nbgv"]);
	}

	[Fact]
	public void NotAdoptable_CarriesNoPlan()
		=> JudgeAt(
				PullRequest(Moved("coverlet.collector", "8.0.1", "10.0.0")),
				Ctx(Packages(("coverlet.collector", "8.0.1"))),
				DependabotTriageService.AdoptAfter - TimeSpan.FromDays(1))
			.Adoption.Should().BeNull("only an adoptable verdict carries what to write");
}
