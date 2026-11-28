using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// The regression guard must recognise the commits the app really writes. Each test makes a real commit
/// through <c>LocalRepoService.CommitToPullRequestBranchAsync</c> with the subject the publish executor
/// uses, reads it back with the guard's own log reader, and asks <see cref="RegressionAttribution"/>;
/// nothing here is a hand-typed subject.
/// </summary>
/// <remarks>
/// Real git, no network: the origin is a bare repository nested in the clone. The pull request merging
/// is stood in for by fast-forwarding main to the delivered commit, as a merge that keeps its message.
/// </remarks>
public sealed class GovernanceCommitAttributionTests : IDisposable
{
	private const string Owner = "panoramicdata";
	private const string Name = "attribution-test";
	private const string Identity = $"{Owner}/{Name}";

	private readonly string _root = Path.Combine(Path.GetTempPath(), "nugetmanagement-tests", Guid.NewGuid().ToString("n"));
	private readonly LocalRepoService _localRepo;

	public GovernanceCommitAttributionTests()
	{
		var appSettings = Options.Create(new AppSettings { LocalReposRoot = _root });
		var runtimeSettings = new RuntimeSettingsService(appSettings, NullLogger<RuntimeSettingsService>.Instance);
		_localRepo = new LocalRepoService(runtimeSettings, NullLogger<LocalRepoService>.Instance);
		CreateClone();
	}

	private string Clone => Path.Combine(_root, Owner, Name);

	private string Origin => Path.Combine(Clone, Owner, Name);

	[Fact]
	public async Task ACommitTheAppMakes_IsIdentifiedAsOurs()
	{
		await CommitAsRemediationAsync("SECURITY.md", "# Security\n", ["COM-01"]).ConfigureAwait(true);

		var commits = await _localRepo.GetRecentCommitsAsync(Identity, 10, TestContext.Current.CancellationToken).ConfigureAwait(true);
		var attribution = RegressionAttribution.Identify(commits);

		attribution.Ours.Should().HaveCount(1, "the guard cannot attribute a regression to a commit it does not recognise");
		attribution.LastGoodRef.Should().Be($"{commits[0].Hash}~1");
	}

	[Fact]
	public async Task ACommitTheAppMakes_CarriesTheTrailerBodyAndPackageMoves()
	{
		File.WriteAllText(Path.Combine(Clone, "Directory.Packages.props"), "<Project><ItemGroup>\n<PackageVersion Include=\"Serilog\" Version=\"3.0.0\" />\n</ItemGroup></Project>\n");
		Git(Clone, "add", "-A");
		Git(Clone, "commit", "-q", "-m", "seed packages");
		File.WriteAllText(Path.Combine(Clone, "Directory.Packages.props"), "<Project><ItemGroup>\n<PackageVersion Include=\"Serilog\" Version=\"4.0.0\" />\n</ItemGroup></Project>\n");

		var push = await _localRepo.CommitToPullRequestBranchAsync(Identity, GovernanceCommitMessage.Subject(Identity), ["PKG-05", "CQ-05"], null, TestContext.Current.CancellationToken).ConfigureAwait(true);

		push.Success.Should().BeTrue(push.Output);
		Git(Clone, "merge", "-q", "--ff-only", push.HeadSha!);
		var body = Git(Clone, "log", "-1", "--format=%B");
		body.Should().Contain("Rules: CQ-05, PKG-05");
		body.Should().Contain("- Directory.Packages.props");
		body.Should().Contain("- Serilog 3.0.0 -> 4.0.0");
		var commits = await _localRepo.GetRecentCommitsAsync(Identity, 1, TestContext.Current.CancellationToken).ConfigureAwait(true);
		GovernanceCommitMessage.ReadRuleIds(commits[0].RemediationTrailer).Should().Equal("CQ-05", "PKG-05");
	}

	[Fact]
	public async Task ACommitWithTheTrailerButAnotherSubject_IsStillOurs()
	{
		await CommitAsRemediationAsync("a.txt", "a\n", ["CQ-05"], "Tidy up the repository").ConfigureAwait(true);

		var commits = await _localRepo.GetRecentCommitsAsync(Identity, 10, TestContext.Current.CancellationToken).ConfigureAwait(true);

		RegressionAttribution.Identify(commits).Ours.Should().HaveCount(1);
	}

	[Fact]
	public async Task AHumanCommitOnTopOfOurs_DoesNotHideThem()
	{
		await CommitAsRemediationAsync("a.txt", "a\n", ["CQ-05"]).ConfigureAwait(true);
		File.WriteAllText(Path.Combine(Clone, "human.txt"), "h\n");
		Git(Clone, "add", "-A");
		Git(Clone, "commit", "-q", "-m", "a human change");

		var commits = await _localRepo.GetRecentCommitsAsync(Identity, 10, TestContext.Current.CancellationToken).ConfigureAwait(true);
		var attribution = RegressionAttribution.Identify(commits);

		commits[0].Subject.Should().Be("a human change");
		attribution.Ours.Should().ContainSingle().Which.Hash.Should().Be(commits[1].Hash);
		attribution.NewestIsHead.Should().BeFalse();
		attribution.LastGoodRef.Should().Be($"{commits[1].Hash}~1");
	}

	[Fact]
	public async Task AHumanRevertOfOurCommit_LeavesOursBelowTheTip()
	{
		await CommitAsRemediationAsync("a.txt", "a\n", ["CQ-05"]).ConfigureAwait(true);
		Git(Clone, "revert", "--no-edit", "HEAD");

		var commits = await _localRepo.GetRecentCommitsAsync(Identity, 10, TestContext.Current.CancellationToken).ConfigureAwait(true);

		commits[0].Subject.Should().StartWith("Revert");
		var attribution = RegressionAttribution.Identify(commits);
		attribution.Ours.Should().ContainSingle("the original is still ours, but the revert above it is not");
		attribution.NewestIsHead.Should().BeFalse("so the guard must show the original fails before blaming it");
	}

	private async Task CommitAsRemediationAsync(string file, string content, string[] ruleIds, string? subject = null)
	{
		File.WriteAllText(Path.Combine(Clone, file), content);
		var push = await _localRepo.CommitToPullRequestBranchAsync(Identity, subject ?? GovernanceCommitMessage.Subject(Identity), ruleIds, null, TestContext.Current.CancellationToken).ConfigureAwait(true);
		push.Success.Should().BeTrue(push.Output);
		Git(Clone, "merge", "-q", "--ff-only", push.HeadSha!);
	}

	private void CreateClone()
	{
		Directory.CreateDirectory(Clone);
		Git(Clone, "init", "-q", "-b", "main");
		Git(Clone, "config", "core.autocrlf", "false");
		Git(Clone, "config", "user.name", "Test");
		Git(Clone, "config", "user.email", "test@example.com");
		File.WriteAllText(Path.Combine(Clone, "README.md"), "seed\n");
		Git(Clone, "add", "-A");
		Git(Clone, "commit", "-q", "-m", "first");

		Directory.CreateDirectory(Origin);
		Git(Origin, "init", "-q", "--bare", "-b", "main");
		File.AppendAllText(Path.Combine(Clone, ".git", "info", "exclude"), $"{Owner}/\n");
		Git(Clone, "remote", "add", "origin", Identity);
		Git(Clone, "push", "-q", "-u", "origin", "main");
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
