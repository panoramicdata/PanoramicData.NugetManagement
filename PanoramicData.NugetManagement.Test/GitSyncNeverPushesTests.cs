using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Git Sync brings a clone up to date with origin and never pushes: with branch protection on, main
/// only changes through a pull request.
/// </summary>
/// <remarks>Real git, no network: the origin is a bare repository nested in the clone.</remarks>
public sealed class GitSyncNeverPushesTests : IDisposable
{
	private const string Owner = "panoramicdata";
	private const string Name = "sync-test";
	private const string Identity = $"{Owner}/{Name}";

	private readonly string _root = Path.Combine(Path.GetTempPath(), "nugetmanagement-tests", Guid.NewGuid().ToString("n"));
	private readonly LocalRepoService _localRepo;

	public GitSyncNeverPushesTests()
	{
		var appSettings = Options.Create(new AppSettings { LocalReposRoot = _root });
		var runtimeSettings = new RuntimeSettingsService(appSettings, NullLogger<RuntimeSettingsService>.Instance);
		_localRepo = new LocalRepoService(runtimeSettings, NullLogger<LocalRepoService>.Instance);
		CreateClone();
	}

	private string Clone => Path.Combine(_root, Owner, Name);

	private string Origin => Path.Combine(Clone, Owner, Name);

	[Fact]
	public async Task WithLocalCommitsAhead_PushesNothingAndSaysCommitAndPushWillDeliverThem()
	{
		var originBefore = Git(Origin, "rev-parse", "main");
		File.WriteAllText(Path.Combine(Clone, "local.txt"), "local\n");
		Git(Clone, "add", "-A");
		Git(Clone, "commit", "-q", "-m", "local work");
		var lines = new List<string>();

		var (success, _) = await _localRepo.GitSyncAsync(Identity, lines.Add, TestContext.Current.CancellationToken);

		success.Should().BeTrue("the sync itself worked");
		Git(Origin, "rev-parse", "main").Should().Be(originBefore, "Git Sync must never push main");
		lines.Should().Contain(line => line.Contains("1 local commit", StringComparison.Ordinal)
			&& line.Contains("pull request", StringComparison.Ordinal));
	}

	[Fact]
	public async Task WhenOriginHasMovedOn_FastForwardsTheClone()
	{
		var other = Path.Combine(_root, "other");
		Git(_root, "clone", "-q", Origin, other);
		Git(other, "config", "user.name", "Test");
		Git(other, "config", "user.email", "test@example.com");
		File.WriteAllText(Path.Combine(other, "theirs.txt"), "theirs\n");
		Git(other, "add", "-A");
		Git(other, "commit", "-q", "-m", "their work");
		Git(other, "push", "-q", "origin", "main");

		var (success, output) = await _localRepo.GitSyncAsync(Identity, null, TestContext.Current.CancellationToken);

		success.Should().BeTrue(output);
		Git(Clone, "rev-parse", "main").Should().Be(Git(Origin, "rev-parse", "main"));
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
