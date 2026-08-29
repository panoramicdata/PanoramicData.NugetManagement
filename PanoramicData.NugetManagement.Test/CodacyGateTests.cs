using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for what CQ-03 says when it cannot reach Codacy. A badge in a README says somebody once set
/// Codacy up; it says nothing about the code's quality today. Treating it as a pass hid a token that
/// could not see a single repository, with 69 repositories reporting green on that basis.
/// </summary>
public class CodacyGateTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _badgeReadme = """
		# Acme.Lib

		[![Codacy Badge](https://app.codacy.com/project/badge/Grade/abc123)](https://app.codacy.com/gh/acme/Acme.Lib)
		""";

	[Fact]
	public async Task ShouldBeInconclusive_WhenTheApiFailsButABadgeIsPresent()
	{
		// An unreachable API is what a bad token looks like, and it must not be reported as compliance.
		var result = await Rule().EvaluateAsync(
			CreateContext(withBadge: true, withApiToken: true),
			CancellationToken.None);

		result.IsApplicable.Should().BeFalse("the gate was never evaluated");
		result.Message.Should().Contain("could not be evaluated");
	}

	[Fact]
	public async Task ShouldStillFail_WhenThereIsNoCodacyEvidenceAtAll()
	{
		// Absence of a .codacy.yml or badge is a fact that needs no API call, so it stays a failure.
		var result = await Rule().EvaluateAsync(
			CreateContext(withBadge: false, withApiToken: true),
			CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.IsApplicable.Should().BeTrue();
	}

	[Fact]
	public async Task ShouldPassOnLocalEvidence_WhenNoApiTokenIsConfiguredAtAll()
	{
		// Without a token there is no API path to fail: the rule is a file check, and a badge is the
		// evidence it asks for. This is the one case where local evidence is the whole standard.
		var result = await Rule().EvaluateAsync(
			CreateContext(withBadge: true, withApiToken: false),
			CancellationToken.None);

		result.Passed.Should().BeTrue();
		result.IsApplicable.Should().BeTrue();
	}

	private static CodacyConfiguredRule Rule() => new();

	private static RepositoryContext CreateContext(bool withBadge, bool withApiToken)
	{
		var files = new Dictionary<string, string>
		{
			["Acme.Lib/Acme.Lib.csproj"] = "<Project/>",
			["README.md"] = withBadge ? _badgeReadme : "# Acme.Lib\n\nNothing to see here."
		};

		var options = new RepoOptions();
		if (withApiToken)
		{
			// A token that will not authenticate: the point is that the call throws, which is what a
			// lapsed token does in production.
			options.Codacy = new CodacyOptions { ApiToken = "not-a-real-token" };
		}

		return new RepositoryContext
		{
			FullName = "acme/Acme.Lib",
			Name = "Acme.Lib",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = options,
			FilePaths = [.. files.Keys],
			FileContents = files
		};
	}
}
