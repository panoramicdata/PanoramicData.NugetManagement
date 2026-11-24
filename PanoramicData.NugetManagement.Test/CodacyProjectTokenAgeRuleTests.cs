using Microsoft.Extensions.Time.Testing;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// CQ-09: warn before the Codacy project token expires. The token lasts a year and the upload that
/// uses it is continue-on-error, so expiry is otherwise silent.
/// </summary>
public class CodacyProjectTokenAgeRuleTests
{
	private const string SecretName = "CODACY_PROJECT_TOKEN";
	private static readonly DateTimeOffset _now = new(2027, 6, 1, 12, 0, 0, TimeSpan.Zero);

	private static RepositoryContext ContextWith(
		IReadOnlyDictionary<string, DateTimeOffset>? updatedAt,
		CodacyOptions? codacy = null,
		bool hasTestProject = true)
		=> new()
		{
			FullName = "test-org/Acme.Widget",
			Name = "Acme.Widget",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions { Codacy = codacy },
			FilePaths = hasTestProject
				? ["Acme.Widget.Test/Acme.Widget.Test.csproj"]
				: ["Acme.Widget/Acme.Widget.csproj"],
			FileContents = new() { ["Acme.Widget.Test/Acme.Widget.Test.csproj"] = "<Project/>" },
			ActionsSecretUpdatedAt = updatedAt
		};

	private static async Task<RuleResult> EvaluateAsync(
		IReadOnlyDictionary<string, DateTimeOffset>? updatedAt,
		CodacyOptions? codacy = null,
		bool hasTestProject = true)
		=> await new CodacyProjectTokenAgeRule(new FakeTimeProvider(_now))
			.EvaluateAsync(ContextWith(updatedAt, codacy, hasTestProject), CancellationToken.None)
			.ConfigureAwait(false);

	private static Dictionary<string, DateTimeOffset> Aged(int days, string name = SecretName)
		=> new() { [name] = _now.AddDays(-days) };

	[Fact]
	public async Task NoTestProjects_IsNotApplicable()
		=> (await EvaluateAsync(Aged(400), hasTestProject: false)).IsApplicable.Should().BeFalse();

	[Fact]
	public async Task MetadataThatCouldNotBeRead_IsNotApplicable()
	{
		var result = await EvaluateAsync(null);

		result.IsApplicable.Should().BeFalse();
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task NoTokenSecret_IsNotApplicable()
	{
		var result = await EvaluateAsync(Aged(400, "NUGET_API_KEY"));

		result.IsApplicable.Should().BeFalse();
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task AFreshToken_Passes()
		=> (await EvaluateAsync(Aged(30))).Passed.Should().BeTrue();

	[Fact]
	public async Task JustInsideTheDefaultThreshold_Passes()
		=> (await EvaluateAsync(Aged(CodacyProjectTokenAgeRule.DefaultWarnAfterDays))).Passed.Should().BeTrue();

	[Fact]
	public async Task JustPastTheDefaultThreshold_Fails()
		=> (await EvaluateAsync(Aged(CodacyProjectTokenAgeRule.DefaultWarnAfterDays + 1))).Passed.Should().BeFalse();

	[Fact]
	public void TheDefaultThresholdIsAboutElevenMonths()
		=> CodacyProjectTokenAgeRule.DefaultWarnAfterDays.Should().BeInRange(325, 340);

	[Fact]
	public async Task TheThresholdIsConfigurable()
	{
		var strict = new CodacyOptions { TokenWarnAfterDays = 10 };

		(await EvaluateAsync(Aged(11), strict)).Passed.Should().BeFalse();
		(await EvaluateAsync(Aged(9), strict)).Passed.Should().BeTrue();
	}

	[Fact]
	public async Task AnExpiredToken_StillFailsAsAWarning()
	{
		var result = await EvaluateAsync(Aged(400));

		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Warning);
		result.Message.Should().Contain("expired");
	}

	[Fact]
	public async Task TheAdvisoryNamesTheManualRemediationAndNeverMintsAToken()
	{
		var result = await EvaluateAsync(Aged(350));

		result.Advisory.Should().NotBeNull();
		result.Advisory!.Detail.Should().Contain("create-repo-token").And.Contain(SecretName);
	}

	[Fact]
	public async Task TheSecretNameIsMatchedCaseInsensitively()
		=> (await EvaluateAsync(Aged(400, "codacy_project_token"))).Passed.Should().BeFalse();

	[Fact]
	public void RuleIdIsCq09()
		=> new CodacyProjectTokenAgeRule().RuleId.Should().Be("CQ-09");
}
