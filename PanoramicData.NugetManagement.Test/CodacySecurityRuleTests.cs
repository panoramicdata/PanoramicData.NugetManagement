using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the SEC rules, which split Codacy's open security findings by priority so each
/// severity band reports in its own colour: SEC-01 red and blocking, SEC-02 amber, SEC-03 blue.
/// </summary>
public class CodacySecurityRuleTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RepositoryContext Context(CodacyOptions? codacy, string fullName = "panoramicdata/Sample.Api")
		=> new()
		{
			FullName = fullName,
			Name = "Sample.Api",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions { Codacy = codacy },
			FilePaths = [],
			FileContents = []
		};

	private static CodacyOptions Token() => new() { ApiToken = "test-token" };

	private static CodacySecurityFinding Finding(
		CodacySecurityPriority priority,
		string title = "OS command injection",
		string? securityCategory = "CommandInjection",
		string? scanType = "SAST",
		CodacySecuritySlaStatus slaStatus = CodacySecuritySlaStatus.Overdue)
		=> new()
		{
			Id = Guid.NewGuid(),
			Title = title,
			Priority = priority,
			SlaStatus = slaStatus,
			SecurityCategory = securityCategory,
			ScanType = scanType,
			HtmlUrl = "https://app.codacy.com/p/848725/issues/index?resultDataId=1",
			OpenedAt = new DateTimeOffset(2026, 4, 3, 14, 16, 17, TimeSpan.Zero),
			DueAt = new DateTimeOffset(2026, 5, 3, 14, 16, 17, TimeSpan.Zero)
		};

	private static CodacySecurityReport Tracked(params CodacySecurityFinding[] findings)
		=> new() { IsTracked = true, Findings = findings };

	public static TheoryData<string, AssessmentSeverity> RuleSeverities => new()
	{
		{ "SEC-01", AssessmentSeverity.Error },
		{ "SEC-02", AssessmentSeverity.Warning },
		{ "SEC-03", AssessmentSeverity.Info }
	};

	private static RuleBase RuleFor(string ruleId, ICodacySecurityService service) => ruleId switch
	{
		"SEC-01" => new CriticalSecurityFindingsRule(service),
		"SEC-02" => new HighSecurityFindingsRule(service),
		"SEC-03" => new OtherSecurityFindingsRule(service),
		_ => throw new ArgumentOutOfRangeException(nameof(ruleId))
	};

	[Theory]
	[MemberData(nameof(RuleSeverities))]
	public void Rule_CarriesTheSeverityItsColourDependsOn(string ruleId, AssessmentSeverity expected)
	{
		var rule = RuleFor(ruleId, new FakeService(Tracked()));

		rule.Severity.Should().Be(expected);
		rule.Category.Should().Be(AssessmentCategory.Security);
	}

	[Fact]
	public async Task Sec01_FailsOnCriticalFindingsOnly()
	{
		var service = new FakeService(Tracked(
			Finding(CodacySecurityPriority.Critical),
			Finding(CodacySecurityPriority.High),
			Finding(CodacySecurityPriority.Medium),
			Finding(CodacySecurityPriority.Low)));

		var result = await new CriticalSecurityFindingsRule(service)
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Error);
		result.Message.Should().Contain("1");
		result.Advisory.Should().NotBeNull();
		result.Advisory!.Detail.Should().Contain("OS command injection");
	}

	[Fact]
	public async Task Sec02_FailsOnHighFindingsOnly()
	{
		var service = new FakeService(Tracked(
			Finding(CodacySecurityPriority.Critical),
			Finding(CodacySecurityPriority.High),
			Finding(CodacySecurityPriority.High),
			Finding(CodacySecurityPriority.Low)));

		var result = await new HighSecurityFindingsRule(service)
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Warning);
		result.Message.Should().Contain("2");
	}

	[Fact]
	public async Task Sec03_FailsOnMediumAndLowTogether()
	{
		var service = new FakeService(Tracked(
			Finding(CodacySecurityPriority.Critical),
			Finding(CodacySecurityPriority.Medium),
			Finding(CodacySecurityPriority.Low)));

		var result = await new OtherSecurityFindingsRule(service)
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Info);
		result.Message.Should().Contain("2");
	}

	[Theory]
	[MemberData(nameof(RuleSeverities))]
	public async Task Rule_PassesWhenNoFindingInItsBand(string ruleId, AssessmentSeverity _)
	{
		// Every rule sees the same report; only the band it owns decides its verdict.
		var service = new FakeService(Tracked(Finding(CodacySecurityPriority.Critical)));
		var rule = RuleFor(ruleId, service);

		var result = await rule.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Passed.Should().Be(ruleId != "SEC-01");
	}

	[Theory]
	[MemberData(nameof(RuleSeverities))]
	public async Task Rule_IsNotApplicableWhenCodacyIsNotConfigured(string ruleId, AssessmentSeverity _)
	{
		var rule = RuleFor(ruleId, new FakeService(Tracked(Finding(CodacySecurityPriority.Critical))));

		var result = await rule.EvaluateAsync(Context(codacy: null), TestContext.Current.CancellationToken);

		result.Passed.Should().BeTrue();
		result.IsApplicable.Should().BeFalse();
	}

	[Theory]
	[MemberData(nameof(RuleSeverities))]
	public async Task Rule_PassesWhenRepositoryIsNotTrackedByCodacy(string ruleId, AssessmentSeverity _)
	{
		var rule = RuleFor(ruleId, new FakeService(new CodacySecurityReport { IsTracked = false }));

		var result = await rule.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Passed.Should().BeTrue();
		result.Message.Should().Contain("not tracked");
	}

	[Theory]
	[MemberData(nameof(RuleSeverities))]
	public async Task Rule_PassesWhenCodacyIsUnreachable(string ruleId, AssessmentSeverity _)
	{
		// An unreachable Codacy must not manufacture a security failure.
		var rule = RuleFor(ruleId, new ThrowingService());

		var result = await rule.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Passed.Should().BeTrue();
	}

	[Theory]
	[MemberData(nameof(RuleSeverities))]
	public async Task Rule_PassesWhenFullNameIsNotOrganizationSlashRepository(string ruleId, AssessmentSeverity _)
	{
		var rule = RuleFor(ruleId, new FakeService(Tracked(Finding(CodacySecurityPriority.Critical))));

		var result = await rule.EvaluateAsync(Context(Token(), fullName: "Sample.Api"), TestContext.Current.CancellationToken);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task Rule_AsksCodacyForTheOrganizationAndRepositorySeparately()
	{
		var service = new FakeService(Tracked());

		_ = await new CriticalSecurityFindingsRule(service)
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		service.Organization.Should().Be("panoramicdata");
		service.Repository.Should().Be("Sample.Api");
	}

	[Fact]
	public async Task Sec01_AdvisoryNamesTheFindingsAndLinksToThem()
	{
		var service = new FakeService(Tracked(
			Finding(CodacySecurityPriority.Critical, "SQL Injection", "SQLInjection"),
			Finding(CodacySecurityPriority.Critical, "Weak random number generator", "Cryptography")));

		var result = await new CriticalSecurityFindingsRule(service)
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		var detail = result.Advisory!.Detail;
		detail.Should().Contain("SQL Injection");
		detail.Should().Contain("Weak random number generator");
		detail.Should().Contain("https://app.codacy.com/p/848725/issues/index?resultDataId=1");
		result.Advisory.Data.Should().ContainKey("finding_count");
		result.Advisory.Data["finding_count"].Should().Be(2);
	}

	[Fact]
	public async Task Sec01_GroupsTheSummaryBySecurityCategory()
	{
		var service = new FakeService(Tracked(
			Finding(CodacySecurityPriority.Critical, "SQL Injection A", "SQLInjection"),
			Finding(CodacySecurityPriority.Critical, "SQL Injection B", "SQLInjection"),
			Finding(CodacySecurityPriority.Critical, "Weak random", "Cryptography")));

		var result = await new CriticalSecurityFindingsRule(service)
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Message.Should().Contain("2 SQLInjection");
		result.Message.Should().Contain("1 Cryptography");
	}

	[Fact]
	public async Task Findings_WithNoSecurityCategory_AreReportedAsUncategorised()
	{
		var service = new FakeService(Tracked(
			Finding(CodacySecurityPriority.Critical, "Something", securityCategory: null)));

		var result = await new CriticalSecurityFindingsRule(service)
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Passed.Should().BeFalse("an uncategorised finding is still a finding");
		result.Message.Should().Contain("Uncategorised");
	}

	private static Task<RuleResult> Sec01(params CodacySecurityFinding[] findings)
		=> new CriticalSecurityFindingsRule(new FakeService(Tracked(findings)))
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

	[Fact]
	public async Task Advisory_GroupsFindingsByHowLateTheyAre_MostUrgentFirst()
	{
		var result = await Sec01(
			Finding(CodacySecurityPriority.Critical, "Fine one", slaStatus: CodacySecuritySlaStatus.OnTrack),
			Finding(CodacySecurityPriority.Critical, "Late one", slaStatus: CodacySecuritySlaStatus.Overdue),
			Finding(CodacySecurityPriority.Critical, "Soon one", slaStatus: CodacySecuritySlaStatus.DueSoon));

		var detail = result.Advisory!.Detail;
		detail.Should().Contain("## Overdue (1)").And.Contain("## Due soon (1)").And.Contain("## On track (1)");
		detail.IndexOf("## Overdue", StringComparison.Ordinal)
			.Should().BeLessThan(detail.IndexOf("## Due soon", StringComparison.Ordinal));
		detail.IndexOf("## Due soon", StringComparison.Ordinal)
			.Should().BeLessThan(detail.IndexOf("## On track", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Advisory_OmitsAnSlaGroupNothingFallsInto()
	{
		var result = await Sec01(
			Finding(CodacySecurityPriority.Critical, "Fine one", slaStatus: CodacySecuritySlaStatus.OnTrack));

		var detail = result.Advisory!.Detail;
		detail.Should().Contain("## On track (1)");
		detail.Should().NotContain("## Overdue").And.NotContain("## Due soon");
	}

	[Fact]
	public async Task Advisory_KeepsTheCategoryBreakdownInsideEachSlaGroup()
	{
		var result = await Sec01(
			Finding(CodacySecurityPriority.Critical, "SQL A", "SQLInjection", slaStatus: CodacySecuritySlaStatus.Overdue),
			Finding(CodacySecurityPriority.Critical, "SQL B", "SQLInjection", slaStatus: CodacySecuritySlaStatus.Overdue),
			Finding(CodacySecurityPriority.Critical, "Weak random", "Cryptography", slaStatus: CodacySecuritySlaStatus.OnTrack));

		var detail = result.Advisory!.Detail;
		detail.Should().Contain("### SQLInjection (2)").And.Contain("### Cryptography (1)");
	}

	[Fact]
	public async Task Summary_CountsOverdueAndDueSoonSeparately()
	{
		var result = await Sec01(
			Finding(CodacySecurityPriority.Critical, "Late one", slaStatus: CodacySecuritySlaStatus.Overdue),
			Finding(CodacySecurityPriority.Critical, "Late two", slaStatus: CodacySecuritySlaStatus.Overdue),
			Finding(CodacySecurityPriority.Critical, "Soon one", slaStatus: CodacySecuritySlaStatus.DueSoon),
			Finding(CodacySecurityPriority.Critical, "Fine one", slaStatus: CodacySecuritySlaStatus.OnTrack));

		result.Message.Should().Contain("2 overdue").And.Contain("1 due soon");
	}

	[Fact]
	public async Task Summary_SaysNothingAboutTheSlaWhenEverythingIsOnTrack()
	{
		var result = await Sec01(
			Finding(CodacySecurityPriority.Critical, "Fine one", slaStatus: CodacySecuritySlaStatus.OnTrack));

		result.Message.Should().NotContainEquivalentOf("overdue").And.NotContainEquivalentOf("due soon");
	}

	[Fact]
	public async Task AdvisoryData_CountsEverySlaStatusSoTheUiNeedNotParseTheProse()
	{
		var result = await Sec01(
			Finding(CodacySecurityPriority.Critical, "Late one", slaStatus: CodacySecuritySlaStatus.Overdue),
			Finding(CodacySecurityPriority.Critical, "Soon one", slaStatus: CodacySecuritySlaStatus.DueSoon),
			Finding(CodacySecurityPriority.Critical, "Fine one", slaStatus: CodacySecuritySlaStatus.OnTrack),
			Finding(CodacySecurityPriority.Critical, "Fine two", slaStatus: CodacySecuritySlaStatus.OnTrack));

		var data = result.Advisory!.Data;
		data["overdue_count"].Should().Be(1);
		data["due_soon_count"].Should().Be(1);
		data["on_track_count"].Should().Be(2);
	}

	[Fact]
	public void SecurityRules_AreRemotelyGradedButFixableInTheWorkingTree()
	{
		var securityRules = RuleRegistry.Rules
			.Where(rule => rule.Category == AssessmentCategory.Security)
			.ToList();

		securityRules.Select(r => r.RuleId).Should().BeEquivalentTo(["SEC-01", "SEC-02", "SEC-03"]);
		securityRules.Should().AllSatisfy(rule => rule.Should().BeAssignableTo<IRemotelyGraded>());
		securityRules.Should().AllSatisfy(rule => rule.Should().NotBeAssignableTo<IFixedOutsideTheWorkingTree>());
	}

	private sealed class FakeService(CodacySecurityReport report) : ICodacySecurityService
	{
		public string? Organization { get; private set; }

		public string? Repository { get; private set; }

		public Task<CodacySecurityReport> GetReportAsync(
			string apiToken,
			string organizationName,
			string repositoryName,
			CancellationToken cancellationToken)
		{
			Organization = organizationName;
			Repository = repositoryName;
			return Task.FromResult(report);
		}
	}

	private sealed class ThrowingService : ICodacySecurityService
	{
		public Task<CodacySecurityReport> GetReportAsync(
			string apiToken,
			string organizationName,
			string repositoryName,
			CancellationToken cancellationToken)
			=> throw new InvalidOperationException("Codacy unreachable");
	}
}
