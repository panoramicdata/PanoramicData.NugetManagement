using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Remediations;
using PanoramicData.NugetManagement.Web.Remediations.RepositoryHygiene;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// REPO-06 renames master to main on GitHub, where the rename is atomic and retargets open pull
/// requests, and then brings the clone into line. Nothing is pushed to main.
/// </summary>
public class DefaultBranchMainRemediationTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string Repository = "panoramicdata/AlienFx.Api";

	private readonly List<string> _calls = [];

	private (int ExitCode, string Output) Succeed(string fileName, IReadOnlyList<string> arguments)
	{
		var call = $"{fileName} {string.Join(' ', arguments)}";
		_calls.Add(call);
		return (0, call == "git branch --show-current" ? "master\n" : string.Empty);
	}

	[Fact]
	public void ShouldBeRegisteredForRepo06()
		=> new RemediationRegistry().Get("REPO-06").Should().BeOfType<DefaultBranchMainRemediation>();

	[Fact]
	public async Task ShouldRenameOnGitHubThenAlignTheClone()
	{
		var remediation = new DefaultBranchMainRemediation((_, fileName, arguments) => Succeed(fileName, arguments));
		var applied = new List<string>();

		remediation.Apply("ignored", await FailingResultAsync(Repository), applied, null);

		_calls.Should().Equal(
			"git branch --show-current",
			$"gh api -X POST repos/{Repository}/branches/master/rename -f new_name=main",
			"git fetch --prune origin",
			"git branch -m master main",
			"git branch -u origin/main main",
			"git remote set-head origin -a");
		applied.Should().ContainSingle();
		_calls.Should().NotContain(call => call.StartsWith("git push", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ShouldNotClaimSuccessOrTouchTheClone_WhenGitHubRefusesTheRename()
	{
		var remediation = new DefaultBranchMainRemediation((_, fileName, arguments) =>
			fileName == "gh" ? (1, "HTTP 403: Must have admin rights to Repository.") : Succeed(fileName, arguments));
		var applied = new List<string>();
		var messages = new List<string>();

		remediation.Apply("ignored", await FailingResultAsync(Repository), applied, messages.Add);

		applied.Should().BeEmpty("REPO-06 would still fail");
		_calls.Should().NotContain("git branch -m master main");
		messages.Should().Contain(message => message.Contains("admin rights", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ShouldRefuse_WhenTheCloneIsNotOnMaster()
	{
		var remediation = new DefaultBranchMainRemediation((_, fileName, arguments) =>
		{
			_calls.Add($"{fileName} {string.Join(' ', arguments)}");
			return (0, "develop\n");
		});
		var applied = new List<string>();

		remediation.Apply("ignored", await FailingResultAsync(Repository), applied, null);

		applied.Should().BeEmpty();
		_calls.Should().NotContain(call => call.StartsWith("gh ", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("panoramicdata/x; rm -rf /")]
	[InlineData("not-a-repo")]
	[InlineData("a/b/c")]
	public async Task ShouldRefuseARepositoryThatIsNotPlainOwnerName(string repository)
	{
		var remediation = new DefaultBranchMainRemediation((_, fileName, arguments) => Succeed(fileName, arguments));
		var result = await FailingResultAsync(repository);

		remediation.CanRemediate(result).Should().BeFalse();
		remediation.Apply("ignored", result, [], null);
		_calls.Should().BeEmpty("the value is placed on a command line");
	}

	private static async Task<RuleResult> FailingResultAsync(string repository)
	{
		var context = new RepositoryContext
		{
			FullName = repository,
			Name = "repo",
			DefaultBranch = "master",
			CurrentBranch = "master",
			Options = new RepoOptions(),
			FilePaths = [],
			FileContents = new Dictionary<string, string>()
		};

		var result = await RuleRegistry.Rules.Single(r => r.RuleId == "REPO-06")
			.EvaluateAsync(context, CancellationToken.None)
			.ConfigureAwait(false);

		result.Passed.Should().BeFalse();
		return result;
	}
}
