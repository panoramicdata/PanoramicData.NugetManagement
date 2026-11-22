using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

public class CodacyBadgeRuleTests
{
	private const string _projectId = "896c2da514fe4bcfbf271f54cbec908b";
	private const string _realBadge = "https://app.codacy.com/project/badge/Grade/" + _projectId;
	private const string _placeholderBadge = "https://app.codacy.com/project/badge/grade/Acme.Widget";

	private sealed class FakeBadgeService(CodacyBadgeInfo? info, Exception? failure = null) : ICodacyBadgeService
	{
		public Task<CodacyBadgeInfo> GetBadgeAsync(string apiToken, string organizationName, string repositoryName, CancellationToken cancellationToken)
			=> failure is null ? Task.FromResult(info!) : throw failure;
	}

	private static readonly CodacyBadgeInfo _enrolled = new(true, _realBadge);

	[Fact]
	public async Task NoToken_APlaceholderBadge_Fails()
	{
		var result = await Evaluate($"[![Codacy Badge]({_placeholderBadge})](https://app.codacy.com)", info: null, withToken: false);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("not a real one");
	}

	[Fact]
	public async Task NoToken_AMentionWithoutABadge_Fails()
	{
		var result = await Evaluate("We use Codacy for analysis.", info: null, withToken: false);

		result.Passed.Should().BeFalse();
		result.Advisory!.Data.Should().NotContainKey("remediation_type");
	}

	[Fact]
	public async Task NoToken_ACorrectlyFormedBadge_Passes()
		=> (await Evaluate($"[![Codacy Badge]({_realBadge})](https://app.codacy.com)", info: null, withToken: false)).Passed.Should().BeTrue();

	[Fact]
	public async Task NoToken_ACoverageBadgeIsNotAGradeBadge()
		=> (await Evaluate("![c](https://app.codacy.com/project/badge/Coverage/" + _projectId + ")", info: null, withToken: false)).Passed.Should().BeFalse();

	[Fact]
	public async Task WithToken_TheBadgeCodacyReports_Passes()
		=> (await Evaluate($"[![Codacy Badge]({_realBadge})](https://app.codacy.com)", _enrolled)).Passed.Should().BeTrue();

	[Fact]
	public async Task WithToken_AMissingBadge_SuggestsTheBadgeCodacyReports()
	{
		var result = await Evaluate("# Acme.Widget", _enrolled);

		result.Passed.Should().BeFalse();
		result.Advisory!.Data["remediation_type"].Should().Be("prepend_line");
		result.Advisory.Data["line_content"].Should().Be($"[![Codacy Badge]({_realBadge})](https://app.codacy.com/gh/test-org/Acme.Widget/dashboard)");
	}

	[Fact]
	public async Task WithToken_APlaceholderBadge_IsReplacedWithTheRealOne()
	{
		var result = await Evaluate($"[![Codacy Badge]({_placeholderBadge})](https://app.codacy.com)", _enrolled);

		result.Passed.Should().BeFalse();
		result.Advisory!.Data["remediation_type"].Should().Be("replace_in_file");
		result.Advisory.Data["old_text"].Should().Be(_placeholderBadge);
		result.Advisory.Data["new_text"].Should().Be(_realBadge);
	}

	[Fact]
	public async Task WithToken_AWellFormedBadgeForAnotherProject_IsReplaced()
	{
		var other = "https://app.codacy.com/project/badge/Grade/" + new string('a', 32);

		var result = await Evaluate($"![x]({other})", _enrolled);

		result.Passed.Should().BeFalse();
		result.Advisory!.Data["old_text"].Should().Be(other);
	}

	[Fact]
	public async Task WithToken_ARepositoryCodacyDoesNotHold_FailsAsNotEnrolledWithNoBadgeSuggested()
	{
		var result = await Evaluate($"![x]({_realBadge})", new CodacyBadgeInfo(false, null));

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("not enrolled");
		result.Advisory!.Data.Should().NotContainKey("remediation_type");
	}

	[Fact]
	public async Task WithToken_WhenCodacyCannotBeReached_ChecksTheBadgeShapeInstead()
	{
		var rule = new CodacyBadgeRule(new FakeBadgeService(null, new HttpRequestException("down")));

		var passes = await rule.EvaluateAsync(CreateContext($"![x]({_realBadge})", withToken: true), CancellationToken.None);
		var fails = await rule.EvaluateAsync(CreateContext($"![x]({_placeholderBadge})", withToken: true), CancellationToken.None);

		passes.Passed.Should().BeTrue();
		fails.Passed.Should().BeFalse();
	}

	private static Task<RuleResult> Evaluate(string readme, CodacyBadgeInfo? info, bool withToken = true)
		=> new CodacyBadgeRule(new FakeBadgeService(info)).EvaluateAsync(CreateContext(readme, withToken), CancellationToken.None);

	private static RepositoryContext CreateContext(string readme, bool withToken)
		=> new()
		{
			FullName = "test-org/Acme.Widget",
			Name = "Acme.Widget",
			DefaultBranch = "main",
			Options = new RepoOptions { Codacy = withToken ? new CodacyOptions { ApiToken = "not-a-real-token" } : null },
			IsPrivateRepository = false,
			FilePaths = ["README.md"],
			FileContents = new() { ["README.md"] = readme }
		};
}
