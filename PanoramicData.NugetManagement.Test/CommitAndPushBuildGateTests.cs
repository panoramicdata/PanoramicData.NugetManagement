using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Remediations;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// #196: a remediation whose result does not build must never be committed to the default branch, and
/// one that builds reaches it only through a pull request.
/// </summary>
/// <remarks>
/// Real git and a real <c>dotnet build</c>, with no network. The origin is a bare repository nested in
/// the clone and named by the relative URL <c>owner/name</c>, which the identity check accepts. The
/// project is a bare MSBuild file whose Build target fails when SECURITY.md exists, so applying the
/// real COM-01 remediation is what breaks it.
/// </remarks>
public sealed class CommitAndPushBuildGateTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private const string Owner = "panoramicdata";
	private const string Name = "gate-test";
	private const string Identity = $"{Owner}/{Name}";

	private const string BrokenBySecurityMd =
		"<Project DefaultTargets=\"Build\"><Target Name=\"Restore\" /><Target Name=\"Build\">"
		+ "<Error Condition=\"Exists('SECURITY.md')\" Text=\"SECURITY.md breaks this build\" /></Target></Project>";

	private const string AlwaysBuilds =
		"<Project DefaultTargets=\"Build\"><Target Name=\"Restore\" /><Target Name=\"Build\" /></Project>";

	private readonly string _root = Path.Combine(Path.GetTempPath(), "nugetmanagement-tests", Guid.NewGuid().ToString("n"));

	private readonly FakeGitHubWriteApi _github = new();

	private string Clone => Path.Combine(_root, Owner, Name);

	private string Origin => Path.Combine(Clone, Owner, Name);

	[Fact]
	public async Task CommitAndPush_WhenTheRemediationBreaksTheBuild_CommitsNothingAndRestoresTheClone()
	{
		var dashboard = CreateClone(BrokenBySecurityMd);
		var row = Row();
		var headBefore = Git(Clone, "rev-parse", "HEAD");
		var originBefore = Git(Origin, "rev-parse", "main");

		var applied = await dashboard.ApplyRuleRemediationAsync(row, SecurityMdMissing(), null, TestContext.Current.CancellationToken);
		applied.Should().NotBeEmpty("the remediation has to have written something for the gate to have anything to refuse");

		var push = await dashboard.CommitAndPushAsync(row, "NuGet governance remediation for test", _github, null, TestContext.Current.CancellationToken);

		push.Success.Should().BeFalse();
		push.WasRefused.Should().BeTrue();
		push.ChangesReverted.Should().BeTrue();
		push.RefusalReason.Should().Contain("does not build").And.Contain("SECURITY.md");
		Git(Clone, "rev-parse", "HEAD").Should().Be(headBefore, "nothing may be committed");
		Git(Origin, "rev-parse", "main").Should().Be(originBefore, "nothing may be pushed");
		Git(Clone, "status", "--porcelain").Should().BeEmpty("the remediation's changes are reverted");
		File.Exists(Path.Combine(Clone, "SECURITY.md")).Should().BeFalse();
		Git(Clone, "stash", "list").Should().NotBeEmpty("the reverted changes are set aside, not thrown away");
		_github.Calls.Should().BeEmpty("no pull request is opened for a change that does not build");
	}

	[Fact]
	public async Task CommitAndPush_WhenTheRemediationBuilds_DeliversItAsAPullRequestAndLeavesMainAlone()
	{
		var dashboard = CreateClone(AlwaysBuilds);
		var row = Row();
		var originBefore = Git(Origin, "rev-parse", "main");

		await dashboard.ApplyRuleRemediationAsync(row, SecurityMdMissing(), null, TestContext.Current.CancellationToken);

		var push = await dashboard.CommitAndPushAsync(row, "NuGet governance remediation for test", _github, null, TestContext.Current.CancellationToken);

		push.Success.Should().BeTrue(push.RefusalReason ?? push.FailureReason);
		var pullRequest = push.PullRequest!;
		pullRequest.Should().NotBeNull();
		pullRequest.Branch.Should().StartWith("gov/");
		pullRequest.Number.Should().Be(7);
		pullRequest.Url.Should().Be($"https://github.com/{Identity}/pull/7");
		pullRequest.Landing.Should().Be(PullRequestLanding.AutoMergeEnabled);
		Git(Origin, "rev-parse", "main").Should().Be(originBefore, "main is only ever changed by the pull request");
		Git(Origin, "rev-parse", pullRequest.Branch).Should().Be(pullRequest.HeadSha);
		Git(Origin, "show", "--name-only", "--format=", pullRequest.Branch).Should().Contain("SECURITY.md");
		Git(Clone, "rev-parse", "main").Should().Be(Git(Clone, "rev-parse", "origin/main"), "local main goes back to origin's");
		Git(Clone, "branch", "--show-current").Should().Be("main");
		Git(Clone, "status", "--porcelain").Should().BeEmpty();
		Git(Clone, "branch", "--list", "gov/*").Should().BeEmpty("the local branch is not kept once it is on origin");
		_github.Calls.Should().Equal($"pr:{pullRequest.Branch}->main", $"automerge:7:{pullRequest.HeadSha}");
		_github.PullRequestTitle.Should().Be("NuGet governance remediation for test");
		_github.PullRequestBody.Should().Contain(GovernanceCommitMessage.TrailerKey);
		row.HasUnpushedCommits.Should().BeFalse();
		row.IsSyncedWithOrigin.Should().BeTrue();
		row.RemediatedRuleIds.Should().BeEmpty();
	}

	[Fact]
	public async Task CommitAndPush_WithUnpushedLocalCommits_CarriesThemOnThePullRequestBranch()
	{
		var dashboard = CreateClone(AlwaysBuilds);
		var originBefore = Git(Origin, "rev-parse", "main");
		File.WriteAllText(Path.Combine(Clone, "local.txt"), "made here\n");
		Git(Clone, "add", "-A");
		Git(Clone, "commit", "-q", "-m", "a local commit");
		var localCommit = Git(Clone, "rev-parse", "HEAD");

		var push = await dashboard.CommitAndPushAsync(Row(), "NuGet governance remediation for test", _github, null, TestContext.Current.CancellationToken);

		push.Success.Should().BeTrue(push.RefusalReason ?? push.FailureReason);
		push.PullRequest!.HeadSha.Should().Be(localCommit, "with nothing staged the branch is the local commits");
		Git(Origin, "rev-parse", "main").Should().Be(originBefore);
		Git(Clone, "rev-parse", "main").Should().Be(originBefore);
	}

	[Fact]
	public async Task CommitAndPush_WhenTheBranchCannotBePushed_KeepsTheCommitOnLocalMain()
	{
		var dashboard = CreateClone(AlwaysBuilds);
		var row = Row();
		Git(Clone, "remote", "set-url", "--push", "origin", "no/such-remote");
		await dashboard.ApplyRuleRemediationAsync(row, SecurityMdMissing(), null, TestContext.Current.CancellationToken);

		var push = await dashboard.CommitAndPushAsync(row, "NuGet governance remediation for test", _github, null, TestContext.Current.CancellationToken);

		push.Success.Should().BeFalse();
		push.PullRequest.Should().BeNull();
		Git(Clone, "branch", "--show-current").Should().Be("main");
		Git(Clone, "rev-list", "--count", "origin/main..main").Should().Be("1", "the commit stays ahead, for the next attempt to carry");
		Git(Clone, "status", "--porcelain").Should().BeEmpty();
		Git(Clone, "branch", "--list", "gov/*").Should().BeEmpty();
		_github.Calls.Should().BeEmpty();
	}

	[Fact]
	public async Task CommitAndPush_WithNothingToCommit_DoesNotBuildAndOpensNothing()
	{
		// A failing build that would refuse if it ran: a clean, level clone has nothing to verify.
		var dashboard = CreateClone(BrokenBySecurityMd);
		File.WriteAllText(Path.Combine(Clone, "SECURITY.md"), "already here\n");
		Git(Clone, "add", "-A");
		Git(Clone, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "-m", "broken already");
		Git(Clone, "push", "-q", "origin", "main");

		var push = await dashboard.CommitAndPushAsync(Row(), "NuGet governance remediation for test", _github, null, TestContext.Current.CancellationToken);

		push.WasRefused.Should().BeFalse(push.RefusalReason);
		push.NothingToDeliver.Should().BeTrue();
		push.PullRequest.Should().BeNull();
		_github.Calls.Should().BeEmpty();
		Git(Origin, "branch", "--list", "gov/*").Should().BeEmpty();
	}

	private RepositoryDashboardRow Row() => new()
	{
		RepositoryFullName = Identity,
		Organization = Owner,
		LocalPath = Clone,
		IsClonedLocally = true,
		Packages = [new() { PackageId = "Gate.Test", LatestVersion = "1.0.0" }]
	};

	private static RuleResult SecurityMdMissing() => new()
	{
		RuleId = "COM-01",
		RuleName = "SECURITY.md exists",
		Category = AssessmentCategory.CommunityHealth,
		Severity = AssessmentSeverity.Warning,
		Passed = false,
		Message = "SECURITY.md not found at repository root.",
		Advisory = new RuleAdvisory
		{
			Summary = "Create SECURITY.md.",
			Detail = "Create SECURITY.md with the standard security policy content.",
			Data = new()
			{
				["expected_path"] = "SECURITY.md",
				["template_content"] = "# Security Policy\n"
			}
		}
	};

	private DashboardService CreateClone(string project)
	{
		Directory.CreateDirectory(Clone);
		Git(Clone, "init", "-q", "-b", "main");
		Git(Clone, "config", "core.autocrlf", "false");
		File.WriteAllText(Path.Combine(Clone, "build.proj"), project);
		Git(Clone, "add", "-A");
		Git(Clone, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "-m", "first");

		Directory.CreateDirectory(Origin);
		Git(Origin, "init", "-q", "--bare", "-b", "main");
		File.AppendAllText(Path.Combine(Clone, ".git", "info", "exclude"), $"{Owner}/\n");
		Git(Clone, "remote", "add", "origin", Identity);
		Git(Clone, "push", "-q", "-u", "origin", "main");

		// CommitAndPushAsync commits with the ambient identity, which a CI runner may not have.
		Git(Clone, "config", "user.name", "Test");
		Git(Clone, "config", "user.email", "test@example.com");

		return CreateDashboardService();
	}

	private DashboardService CreateDashboardService()
	{
		var appSettings = Options.Create(new AppSettings { LocalReposRoot = _root });
		var runtimeSettings = new RuntimeSettingsService(appSettings, NullLogger<RuntimeSettingsService>.Instance);
		var localRepo = new LocalRepoService(runtimeSettings, NullLogger<LocalRepoService>.Instance);
		var cache = new DashboardCacheService(NullLogger<DashboardCacheService>.Instance, Path.Combine(_root, "dashboard-cache.json"));

		return DashboardTestFactory.Create(appSettings, runtimeSettings, localRepo, cache);
	}

	private static string Git(string workingDirectory, params string[] arguments)
	{
		var (exitCode, output) = LocalRepoService
			.RunCommandAsync(workingDirectory, "git", arguments, CancellationToken.None)
			.GetAwaiter()
			.GetResult();

		return exitCode == 0
			? output.Trim()
			: throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {output}");
	}

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_root))
			{
				// Git marks its object files read-only, which Directory.Delete will not remove.
				foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
				{
					File.SetAttributes(file, FileAttributes.Normal);
				}

				Directory.Delete(_root, recursive: true);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// A leftover temp directory is not worth failing a test over.
		}

		GC.SuppressFinalize(this);
	}
}
