using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Remediations;
using PanoramicData.NugetManagement.Web.Remediations.CommunityHealth;

namespace PanoramicData.NugetManagement.Test;

public class SponsorshipsRemediationTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string NodeId = "R_kgDOR2GqOQ";

	[Fact]
	public void ShouldBeRegisteredForCom06()
		=> new RemediationRegistry().Get("COM-06").Should().BeOfType<SponsorshipsRemediation>();

	[Fact]
	public async Task ShouldLookUpTheNodeIdThenEnableAndRecordItAsApplied()
	{
		var calls = new List<IReadOnlyList<string>>();
		var remediation = new SponsorshipsRemediation((_, args) =>
		{
			calls.Add(args);
			return calls.Count == 1
				? (0, NodeId + "\n")
				: (0, "{\"data\":{\"updateRepository\":{\"repository\":{\"hasSponsorshipsEnabled\":true}}}}");
		});
		var applied = new List<string>();

		remediation.Apply("ignored", await FailingResultAsync("panoramicdata/AlienFx.Api"), applied, null);

		calls.Should().HaveCount(2);
		calls[0].Should().Equal("api", "repos/panoramicdata/AlienFx.Api", "--jq", ".node_id");
		calls[1].Should().Contain("graphql").And.Contain($"id={NodeId}");
		applied.Should().ContainSingle();
	}

	[Fact]
	public async Task ShouldNotClaimSuccess_WhenGitHubRefusesTheMutation()
	{
		var calls = 0;
		var remediation = new SponsorshipsRemediation((_, _) => ++calls == 1
			? (0, NodeId)
			: (1, "Must have admin rights to Repository."));
		var applied = new List<string>();
		var messages = new List<string>();

		remediation.Apply("ignored", await FailingResultAsync("panoramicdata/AlienFx.Api"), applied, messages.Add);

		applied.Should().BeEmpty("COM-06 would still fail, so reporting it applied would hide that");
		messages.Should().ContainSingle().Which.Should().Contain("admin rights");
	}

	[Fact]
	public async Task ShouldNotClaimSuccess_WhenTheMutationLeavesItOff()
	{
		var calls = 0;
		var remediation = new SponsorshipsRemediation((_, _) => ++calls == 1
			? (0, NodeId)
			: (0, "{\"data\":{\"updateRepository\":{\"repository\":{\"hasSponsorshipsEnabled\":false}}}}"));
		var applied = new List<string>();

		remediation.Apply("ignored", await FailingResultAsync("panoramicdata/AlienFx.Api"), applied, null);

		applied.Should().BeEmpty();
	}

	[Fact]
	public async Task ShouldNotRunTheMutation_WhenTheNodeIdIsNotPlain()
	{
		var calls = 0;
		var remediation = new SponsorshipsRemediation((_, _) => ++calls == 1 ? (0, "x y; z") : (0, string.Empty));

		remediation.Apply("ignored", await FailingResultAsync("panoramicdata/AlienFx.Api"), [], null);

		calls.Should().Be(1);
	}

	[Theory]
	[InlineData("panoramicdata/x; rm -rf /")]
	[InlineData("not-a-repo")]
	[InlineData("a/b/c")]
	public async Task ShouldRefuseARepositoryThatIsNotPlainOwnerName(string repository)
	{
		var ran = false;
		var remediation = new SponsorshipsRemediation((_, _) =>
		{
			ran = true;
			return (0, string.Empty);
		});
		var result = await FailingResultAsync(repository);

		remediation.CanRemediate(result).Should().BeFalse();
		remediation.Apply("ignored", result, [], null);
		ran.Should().BeFalse("the value is placed on a command line");
	}

	private static async Task<RuleResult> FailingResultAsync(string repository)
	{
		var context = new RepositoryContext
		{
			FullName = repository,
			Name = "repo",
			DefaultBranch = "main",
			Options = new RepoOptions(),
			FilePaths = [],
			FileContents = new Dictionary<string, string>(),
			SponsorshipsEnabled = false,
			IsPrivateRepository = false
		};

		var result = await RuleRegistry.Rules.Single(r => r.RuleId == "COM-06")
			.EvaluateAsync(context, CancellationToken.None)
			.ConfigureAwait(false);

		result.Passed.Should().BeFalse();
		return result;
	}
}
