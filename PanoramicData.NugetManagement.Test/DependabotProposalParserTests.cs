using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="DependabotProposalParser"/>: it reads what a Dependabot pull request
/// proposes, and returns null for anything it does not recognise so that triage leaves it alone.
/// </summary>
public class DependabotProposalParserTests(ITestOutputHelper output) : TestWithOutput(output)
{
	/// <summary>
	/// A pull request as the issue list reports it, defaulting to Dependabot's authorship so each
	/// test states only the fact it is about.
	/// </summary>
	private static RepositoryIssue PullRequest(
		string title,
		string author = "dependabot[bot]",
		bool isPullRequest = true,
		string? body = null)
		=> new()
		{
			Number = 1,
			Title = title,
			IsPullRequest = isPullRequest,
			HtmlUrl = "https://github.com/panoramicdata/Athonet.Api/pull/1",
			AuthorLogin = author,
			CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
			Body = body
		};

	[Fact]
	public void Parse_NuGetBumpInSubdirectory_ReadsPackageVersionsAndDirectory()
	{
		var proposal = DependabotProposalParser.Parse(
			PullRequest("Bump refit from 6.3.2 to 7.2.22 in /Athonet.Api"));

		proposal.Should().NotBeNull();
		proposal!.Bumps.Should().HaveCount(1);
		proposal.Bumps[0].Dependency.Should().Be(new DependencyRef(DependencyEcosystem.NuGet, "refit"));
		proposal.Bumps[0].FromVersion.Should().Be("6.3.2");
		proposal.Bumps[0].ToVersion.Should().Be("7.2.22");
		proposal.Bumps[0].Directory.Should().Be("/Athonet.Api");
	}

	[Fact]
	public void Parse_BumpWithNoDirectory_LeavesDirectoryNull()
	{
		var proposal = DependabotProposalParser.Parse(PullRequest("Bump refit from 6.3.2 to 7.2.22"));

		proposal.Should().NotBeNull();
		proposal!.Bumps[0].Directory.Should().BeNull();
	}

	[Fact]
	public void Parse_OwnerSlashNameDependency_IsAGitHubAction()
	{
		var proposal = DependabotProposalParser.Parse(
			PullRequest("Bump actions/setup-dotnet from 1 to 5"));

		proposal.Should().NotBeNull();
		proposal!.Bumps[0].Dependency.Should().Be(
			new DependencyRef(DependencyEcosystem.GitHubActions, "actions/setup-dotnet"),
			"a dependency name containing a slash is an action, not a NuGet package");
		proposal.Bumps[0].FromVersion.Should().Be("1");
		proposal.Bumps[0].ToVersion.Should().Be("5");
	}

	[Fact]
	public void Parse_GroupedTitleWithNoBody_ReturnsNull()
		=> DependabotProposalParser
			.Parse(PullRequest("Bump the nuget group with 3 updates"))
			.Should().BeNull(
				"a grouped title names no versions, and with no body there is nothing else to read — so "
					+ "it is left strictly alone, as it was before bodies were read at all");

	[Fact]
	public void Parse_TitleThatIsNotAVersionBump_ReturnsNull()
		=> DependabotProposalParser
			.Parse(PullRequest("Update dependabot.yml to add a weekly schedule"))
			.Should().BeNull();

	[Fact]
	public void Parse_PullRequestFromAHuman_ReturnsNull()
		=> DependabotProposalParser
			.Parse(PullRequest("Bump refit from 6.3.2 to 7.2.22", author: "davidbond"))
			.Should().BeNull("only Dependabot's own pull requests are eligible for triage");

	[Fact]
	public void Parse_IssueRatherThanPullRequest_ReturnsNull()
		=> DependabotProposalParser
			.Parse(PullRequest("Bump refit from 6.3.2 to 7.2.22", isPullRequest: false))
			.Should().BeNull();

	[Fact]
	public void DependencyRef_ComparesNameCaseInsensitively()
		=> new DependencyRef(DependencyEcosystem.NuGet, "Refit")
			.Should().Be(
				new DependencyRef(DependencyEcosystem.NuGet, "refit"),
				"NuGet package ids and action names are not case-sensitive in practice");

	[Fact]
	public void Parse_RealSingleDependencyBody_ReadsTheOneDependency()
	{
		var proposal = DependabotProposalParser.Parse(PullRequest(
			"Bump coverlet.collector from 8.0.1 to 10.0.0",
			body: DependabotFixtures.Body(6)));

		proposal.Should().NotBeNull();
		proposal!.Bumps.Should().HaveCount(1);
		proposal.Bumps[0].Dependency.Should().Be(
			new DependencyRef(DependencyEcosystem.NuGet, "coverlet.collector"));
		proposal.Bumps[0].FromVersion.Should().Be("8.0.1");
		proposal.Bumps[0].ToVersion.Should().Be(
			"10.0.0",
			"the full stop Dependabot ends the line with is not part of the version");
	}

	[Fact]
	public void Parse_RealGroupedBody_ReadsEveryDependency()
	{
		var proposal = DependabotProposalParser.Parse(PullRequest(
			"Bump Microsoft.Extensions.DependencyInjection and 2 others",
			body: DependabotFixtures.Body(30)));

		proposal.Should().NotBeNull();
		proposal!.Bumps.Select(b => b.Dependency.Name).Should().Equal(
			[
				"Microsoft.Extensions.DependencyInjection",
				"Microsoft.Extensions.Logging",
				"Microsoft.Extensions.Options"
			],
			"the title names only the first of three, and in the order the body lists them");
		proposal.Bumps.Should().AllSatisfy(bump =>
		{
			bump.FromVersion.Should().Be("10.0.8");
			bump.ToVersion.Should().Be("10.0.9");
			bump.Dependency.Ecosystem.Should().Be(DependencyEcosystem.NuGet);
		});
	}

	[Fact]
	public void Parse_EveryCapturedBody_ReadsAsManyBumpsAsItProposes()
	{
		foreach (var (number, expected) in DependabotFixtures.BumpCounts)
		{
			var proposal = DependabotProposalParser.Parse(PullRequest(
				$"Bump something, pull request {number}",
				body: DependabotFixtures.Body(number)));

			proposal.Should().NotBeNull($"pull request #{number}'s real body must be readable");
			proposal!.Bumps.Should().HaveCount(
				expected,
				$"pull request #{number} proposes {expected} move(s), and the release-notes blocks in "
					+ "its body must not be read as extra proposals");
		}
	}

	[Fact]
	public void Parse_BodyNamingAnAction_IsAGitHubAction()
	{
		var proposal = DependabotProposalParser.Parse(PullRequest(
			"Bump the actions group with 1 update",
			body: "Updated [actions/checkout](https://github.com/actions/checkout) from 4 to 5."));

		proposal.Should().NotBeNull();
		proposal!.Bumps[0].Dependency.Should().Be(
			new DependencyRef(DependencyEcosystem.GitHubActions, "actions/checkout"),
			"a dependency name containing a slash is an action, not a NuGet package");
		proposal.Bumps[0].ToVersion.Should().Be("5");
	}

	[Fact]
	public void Parse_BodyInTheBacktickForm_IsStillRead()
	{
		var proposal = DependabotProposalParser.Parse(PullRequest(
			"Bump the nuget group with 1 update",
			body: "Updates `Serilog` from 3.0.0 to 4.0.0"));

		proposal.Should().NotBeNull(
			"the captured bodies all use the markdown-link form, but Dependabot has used backticks "
				+ "elsewhere and reading fewer pull requests is a worse failure than reading none");
		proposal!.Bumps[0].Dependency.Name.Should().Be("Serilog");
		proposal.Bumps[0].ToVersion.Should().Be("4.0.0");
	}

	[Fact]
	public void Parse_BodyNamingADirectory_ReadsIt()
	{
		var proposal = DependabotProposalParser.Parse(PullRequest(
			"Bump the nuget group with 1 update",
			body: "Updated [refit](https://github.com/reactiveui/refit) from 6.3.2 to 7.2.22 in /Athonet.Api."));

		proposal.Should().NotBeNull();
		proposal!.Bumps[0].ToVersion.Should().Be("7.2.22");
		proposal.Bumps[0].Directory.Should().Be("/Athonet.Api");
	}

	[Fact]
	public void Parse_BodyListingTheSameDependencyTwice_KeepsOneBump()
	{
		var proposal = DependabotProposalParser.Parse(PullRequest(
			"Bump the nuget group with 1 update",
			body: """
				Updated [Serilog](https://github.com/serilog/serilog) from 3.0.0 to 4.0.0.
				Updated [Serilog](https://github.com/serilog/serilog) from 3.0.0 to 4.0.0.
				"""));

		proposal.Should().NotBeNull();
		proposal!.Bumps.Should().HaveCount(
			1,
			"Dependabot repeats a dependency in the body when it appears in more than one manifest, and "
				+ "a duplicated bump would be written twice and counted twice");
	}

	[Fact]
	public void Parse_BodyWithNoRecognisableLine_ReturnsNull()
		=> DependabotProposalParser
			.Parse(PullRequest("Bump the nuget group with 3 updates", body: "Some prose and a table."))
			.Should().BeNull(
				"failing silent is the whole design: a body we cannot read must not become a proposal "
					+ "we act on");

	[Fact]
	public void Parse_BodyThatIsUnreadableButTitleThatIsNot_FallsBackToTheTitle()
	{
		var proposal = DependabotProposalParser.Parse(PullRequest(
			"Bump refit from 6.3.2 to 7.2.22",
			body: "Some prose with no move in it at all."));

		proposal.Should().NotBeNull(
			"the title fallback is what keeps a single-dependency pull request readable when the body "
				+ "is absent or unhelpful");
		proposal!.Bumps[0].ToVersion.Should().Be("7.2.22");
	}
}
