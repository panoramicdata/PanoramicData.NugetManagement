using Octokit;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests which GitHub author associations count as a maintainer of the repository.
/// </summary>
public class MaintainerAssociationTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Theory]
	[InlineData(AuthorAssociation.Owner)]
	[InlineData(AuthorAssociation.Member)]
	[InlineData(AuthorAssociation.Collaborator)]
	public void WriteAccessMeansMaintainer(AuthorAssociation association)
		=> OctokitGitHubIssueApi.IsMaintainerAssociation(association).Should().BeTrue();

	[Theory]
	[InlineData(AuthorAssociation.Contributor)]
	[InlineData(AuthorAssociation.FirstTimeContributor)]
	[InlineData(AuthorAssociation.FirstTimer)]
	[InlineData(AuthorAssociation.None)]
	public void EveryoneElseIsSomeoneWaitingForAnAnswer(AuthorAssociation association)
		=> OctokitGitHubIssueApi.IsMaintainerAssociation(association).Should().BeFalse();
}
