using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="DependabotTitleParser"/>: it reads what a Dependabot pull request proposes,
/// and returns null for anything it does not recognise so that triage leaves it alone.
/// </summary>
public class DependabotTitleParserTests(ITestOutputHelper output) : TestWithOutput(output)
{
	/// <summary>
	/// A pull request as the issue list reports it, defaulting to Dependabot's authorship so each
	/// test states only the fact it is about.
	/// </summary>
	private static RepositoryIssue PullRequest(
		string title,
		string author = "dependabot[bot]",
		bool isPullRequest = true)
		=> new()
		{
			Number = 1,
			Title = title,
			IsPullRequest = isPullRequest,
			HtmlUrl = "https://github.com/panoramicdata/Athonet.Api/pull/1",
			AuthorLogin = author,
			CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
		};

	[Fact]
	public void Parse_NuGetBumpInSubdirectory_ReadsPackageVersionsAndDirectory()
	{
		var proposal = DependabotTitleParser.Parse(
			PullRequest("Bump refit from 6.3.2 to 7.2.22 in /Athonet.Api"));

		proposal.Should().NotBeNull();
		proposal!.Dependency.Should().Be(new DependencyRef(DependencyEcosystem.NuGet, "refit"));
		proposal.FromVersion.Should().Be("6.3.2");
		proposal.ToVersion.Should().Be("7.2.22");
		proposal.Directory.Should().Be("/Athonet.Api");
	}

	[Fact]
	public void Parse_BumpWithNoDirectory_LeavesDirectoryNull()
	{
		var proposal = DependabotTitleParser.Parse(PullRequest("Bump refit from 6.3.2 to 7.2.22"));

		proposal.Should().NotBeNull();
		proposal!.Directory.Should().BeNull();
	}

	[Fact]
	public void Parse_OwnerSlashNameDependency_IsAGitHubAction()
	{
		var proposal = DependabotTitleParser.Parse(
			PullRequest("Bump actions/setup-dotnet from 1 to 5"));

		proposal.Should().NotBeNull();
		proposal!.Dependency.Should().Be(
			new DependencyRef(DependencyEcosystem.GitHubActions, "actions/setup-dotnet"),
			"a dependency name containing a slash is an action, not a NuGet package");
		proposal.FromVersion.Should().Be("1");
		proposal.ToVersion.Should().Be("5");
	}

	[Fact]
	public void Parse_GroupedBump_ReturnsNull()
		=> DependabotTitleParser
			.Parse(PullRequest("Bump the nuget group with 3 updates"))
			.Should().BeNull("a grouped pull request names no single dependency, so it must be left alone");

	[Fact]
	public void Parse_TitleThatIsNotAVersionBump_ReturnsNull()
		=> DependabotTitleParser
			.Parse(PullRequest("Update dependabot.yml to add a weekly schedule"))
			.Should().BeNull();

	[Fact]
	public void Parse_PullRequestFromAHuman_ReturnsNull()
		=> DependabotTitleParser
			.Parse(PullRequest("Bump refit from 6.3.2 to 7.2.22", author: "davidbond"))
			.Should().BeNull("only Dependabot's own pull requests are eligible for triage");

	[Fact]
	public void Parse_IssueRatherThanPullRequest_ReturnsNull()
		=> DependabotTitleParser
			.Parse(PullRequest("Bump refit from 6.3.2 to 7.2.22", isPullRequest: false))
			.Should().BeNull();

	[Fact]
	public void DependencyRef_ComparesNameCaseInsensitively()
		=> new DependencyRef(DependencyEcosystem.NuGet, "Refit")
			.Should().Be(
				new DependencyRef(DependencyEcosystem.NuGet, "refit"),
				"NuGet package ids and action names are not case-sensitive in practice");
}
