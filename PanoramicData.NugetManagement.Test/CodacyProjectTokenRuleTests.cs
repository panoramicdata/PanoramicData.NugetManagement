using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// CQ-07: the Codacy project token, the one cause of a RED TST-10 that cannot be fixed by editing
/// files.
/// </summary>
public class CodacyProjectTokenRuleTests
{
	private static RepositoryContext ContextWith(
		IReadOnlyList<string>? secretNames,
		bool hasTestProject = true)
		=> new()
		{
			FullName = "test-org/Acme.Widget",
			Name = "Acme.Widget",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = hasTestProject
				? ["Acme.Widget.Test/Acme.Widget.Test.csproj"]
				: ["Acme.Widget/Acme.Widget.csproj"],
			FileContents = new() { ["Acme.Widget.Test/Acme.Widget.Test.csproj"] = "<Project/>" },
			ActionsSecretNames = secretNames
		};

	private static async Task<RuleResult> EvaluateAsync(
		IReadOnlyList<string>? secretNames,
		bool hasTestProject = true)
		=> await new CodacyProjectTokenRule()
			.EvaluateAsync(ContextWith(secretNames, hasTestProject), CancellationToken.None)
			.ConfigureAwait(false);

	[Fact]
	public async Task NoTestProjects_IsNotApplicable()
	{
		var result = await EvaluateAsync(["CODACY_PROJECT_TOKEN"], hasTestProject: false);

		result.IsApplicable.Should().BeFalse();
	}

	[Fact]
	public async Task TheTokenIsPresent_Passes()
	{
		var result = await EvaluateAsync(["NUGET_API_KEY", "CODACY_PROJECT_TOKEN"]);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task TheTokenIsAbsent_Fails()
	{
		var result = await EvaluateAsync(["NUGET_API_KEY"]);

		result.Passed.Should().BeFalse();
		result.Advisory.Should().NotBeNull();
	}

	[Fact]
	public async Task NoSecretsAtAll_Fails()
	{
		var result = await EvaluateAsync([]);

		result.Passed.Should().BeFalse();
	}

	[Fact]
	public async Task SecretsThatCouldNotBeRead_AreUnknownRatherThanMissing()
	{
		// The distinction this rule most needs to keep. A null list means the question went
		// unanswered; reading it as "no token" would fail every repository the moment the assessing
		// credential lost the rights this endpoint needs.
		var result = await EvaluateAsync(null);

		result.IsApplicable.Should().BeFalse();
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task TheSecretNameIsMatchedCaseInsensitively()
	{
		var result = await EvaluateAsync(["codacy_project_token"]);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public void RuleIdIsCq07()
		=> new CodacyProjectTokenRule().RuleId.Should().Be("CQ-07");
}
