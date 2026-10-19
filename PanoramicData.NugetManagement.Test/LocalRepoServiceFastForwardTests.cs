using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that a clone which is only behind origin is brought level before a fix is applied to it, and
/// that a clone with commits of its own is never touched. Run against real throwaway git repositories.
/// </summary>
public class LocalRepoServiceFastForwardTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private const string Identity = "panoramicdata/Example";

	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	private string Origin => Path.Combine(_root, "origin.git");

	private string AppClone => Path.Combine(_root, "repos", "panoramicdata", "Example");

	private string Elsewhere => Path.Combine(_root, "elsewhere");

	[Fact]
	public async Task ACleanCloneBehindOriginShouldBeBroughtLevel()
	{
		var service = CreateServiceWithClones();
		Commit(Elsewhere, "one.txt", "pushed since");
		Git(Elsewhere, "push", "-q", "origin", "main");
		Commit(Elsewhere, "two.txt", "pushed since too");
		Git(Elsewhere, "push", "-q", "origin", "main");

		var (caughtUp, behind) = await service.FastForwardToOriginAsync(Identity, TestContext.Current.CancellationToken);

		caughtUp.Should().BeTrue();
		behind.Should().Be(2);
		File.Exists(Path.Combine(AppClone, "one.txt")).Should().BeTrue();
		File.Exists(Path.Combine(AppClone, "two.txt")).Should().BeTrue();
		Git(AppClone, "rev-parse", "HEAD").Should().Be(Git(AppClone, "rev-parse", "origin/main"));
	}

	[Fact]
	public async Task ACloneAlreadyLevelShouldBeLeftAlone()
	{
		var service = CreateServiceWithClones();
		var before = Git(AppClone, "rev-parse", "HEAD");

		var (caughtUp, behind) = await service.FastForwardToOriginAsync(Identity, TestContext.Current.CancellationToken);

		caughtUp.Should().BeTrue();
		behind.Should().Be(0);
		Git(AppClone, "rev-parse", "HEAD").Should().Be(before);
	}

	[Fact]
	public async Task ACloneWithCommitsOfItsOwnShouldNeverBeMoved()
	{
		var service = CreateServiceWithClones();
		Commit(AppClone, "mine.txt", "local work");
		var before = Git(AppClone, "rev-parse", "HEAD");
		Commit(Elsewhere, "theirs.txt", "pushed since");
		Git(Elsewhere, "push", "-q", "origin", "main");

		var (caughtUp, behind) = await service.FastForwardToOriginAsync(Identity, TestContext.Current.CancellationToken);

		caughtUp.Should().BeFalse("combining histories is a decision, not a catch-up");
		behind.Should().Be(1);
		Git(AppClone, "rev-parse", "HEAD").Should().Be(before);
		File.Exists(Path.Combine(AppClone, "theirs.txt")).Should().BeFalse();
	}

	[Fact]
	public async Task ARepositoryNotClonedShouldNotBeCaughtUp()
	{
		var service = CreateService();

		var (caughtUp, behind) = await service.FastForwardToOriginAsync(Identity, TestContext.Current.CancellationToken);

		caughtUp.Should().BeFalse();
		behind.Should().BeNull();
	}

	private LocalRepoService CreateService()
	{
		Directory.CreateDirectory(_root);
		var settings = new RuntimeSettingsService(
			Options.Create(new AppSettings { NuGetOrganization = "panoramicdata", LocalReposRoot = Path.Combine(_root, "repos") }),
			NullLogger<RuntimeSettingsService>.Instance,
			Path.Combine(_root, "runtime-settings.json"));

		return new LocalRepoService(settings, NullLogger<LocalRepoService>.Instance);
	}

	/// <summary>
	/// An origin with one commit, the app's clone of it where the service expects to find it, and a second
	/// clone standing for whoever else pushes to origin.
	/// </summary>
	private LocalRepoService CreateServiceWithClones()
	{
		var service = CreateService();
		var seed = Path.Combine(_root, "seed");
		Directory.CreateDirectory(seed);

		Git(seed, "init", "-q", "-b", "main");
		Commit(seed, "readme.txt", "first");
		Git(_root, "clone", "-q", "--bare", seed, Origin);
		Git(_root, "clone", "-q", Origin, AppClone);
		Git(_root, "clone", "-q", Origin, Elsewhere);

		return service;
	}

	private static void Commit(string directory, string fileName, string content)
	{
		File.WriteAllText(Path.Combine(directory, fileName), content);
		Git(directory, "add", fileName);
		Git(directory, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "-m", $"add {fileName}");
	}

	private static string Git(string workingDirectory, params string[] arguments)
	{
		var (exitCode, output) = LocalRepoService
			.RunCommandAsync(workingDirectory, "git", arguments)
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
