using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for getting a clone out from behind uncommitted changes: how a stuck clone is recognised, what
/// the model is told, how it is confined, and the git operations that never lose work.
/// </summary>
public class UnstickTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		$"unstick-{Guid.NewGuid():n}");

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
			{
				File.SetAttributes(file, FileAttributes.Normal);
			}

			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private static RepositoryDashboardRow Row(bool cloned, bool? clean, bool? synced) => new()
	{
		RepositoryFullName = "o/Sample",
		IsClonedLocally = cloned,
		IsWorkingTreeClean = clean,
		IsSyncedWithOrigin = synced
	};

	[Theory]
	[InlineData(true, false, false, true)]
	[InlineData(true, false, null, true)]
	[InlineData(true, false, true, false)]
	[InlineData(true, true, false, false)]
	[InlineData(true, null, false, false)]
	[InlineData(false, false, false, false)]
	public void IsStuck_NeedsADirtyTreeThatIsAlsoOutOfStep(bool cloned, bool? clean, bool? synced, bool expected)
		=> AiFixTargets.IsStuck(Row(cloned, clean, synced)).Should().Be(expected);

	[Fact]
	public void PathsIn_ReadsFilesFoldersAndRenames_EvenWhenTheFirstLineLostItsLeadingSpace()
	{
		var (files, folders) = UnstickPrompt.PathsIn(
		[
			"M src/A.cs",
			" M src/B.cs",
			"?? tests/New.cs",
			"?? tests/generated/",
			"R  old/Name.cs -> new/Name.cs",
			"",
			"??"
		]);

		files.Should().BeEquivalentTo("src/A.cs", "src/B.cs", "tests/New.cs", "new/Name.cs");
		folders.Should().BeEquivalentTo("tests/generated");
	}

	[Fact]
	public void BuildTask_NamesTheFilesTheProblemAndThatRestoringIsAcceptable()
	{
		var task = UnstickPrompt.BuildTask("o/Sample", [" M src/A.cs", "?? tests/New.cs"], "Build failed: CS0103.");

		task.Should().Contain("o/Sample")
			.And.Contain("src/A.cs")
			.And.Contain("tests/New.cs")
			.And.Contain("Build failed: CS0103.")
			.And.Contain("restore_file");
	}

	[Fact]
	public void BuildTask_CutsALongListAndSaysHowMuchWasLeftOut()
	{
		var lines = Enumerable.Range(0, UnstickPrompt.MaxFilesListed + 5).Select(i => $" M f{i}.cs").ToList();

		var task = UnstickPrompt.BuildTask("o/Sample", lines, "x");

		task.Should().Contain("...and 5 more.");
	}

	[Fact]
	public void RestoreFile_IsOnlyOfferedWhenThereIsSomethingToRestoreFrom()
	{
		new AiFixToolbox(_root).Offers("restore_file").Should().BeFalse();

		new AiFixToolbox(_root, restoreFile: (_, _) => Task.FromResult("ok")).Offers("restore_file").Should().BeTrue();

		AiFixSession.ToolSpecs.Select(spec => spec.Name).Should().Contain("restore_file");
		AiFixToolbox.ToolNames.Should().Contain("restore_file");
	}

	[Fact]
	public async Task RestoreFile_RunsTheRestoreAndStillLetsTheSessionUndoItself()
	{
		var clone = Path.Combine(_root, "clone");
		Directory.CreateDirectory(clone);
		var file = Path.Combine(clone, "A.cs");
		await File.WriteAllTextAsync(file, "edited by an unreviewed fix", TestContext.Current.CancellationToken).ConfigureAwait(true);

		var toolbox = new AiFixToolbox(
			clone,
			restoreFile: async (path, token) =>
			{
				await File.WriteAllTextAsync(Path.Combine(clone, path), "as committed", token).ConfigureAwait(false);
				return $"Restored {path} to the last commit.";
			});

		var result = await toolbox
			.ExecuteAsync(new AiToolCall("restore_file", new Dictionary<string, string> { ["path"] = "A.cs" }), TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		result.IsError.Should().BeFalse();
		result.Content.Should().Contain("Restored A.cs");
		(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken).ConfigureAwait(true)).Should().Be("as committed");

		toolbox.Undo().Should().Be(1);
		(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken).ConfigureAwait(true)).Should().Be("edited by an unreviewed fix");
	}

	[Fact]
	public async Task RestoreFile_RefusesAPathOutsideTheFilesItMayTouch()
	{
		var clone = Path.Combine(_root, "clone");
		Directory.CreateDirectory(clone);
		var called = false;

		var toolbox = new AiFixToolbox(
			clone,
			writablePaths: ["A.cs"],
			restoreFile: (_, _) =>
			{
				called = true;
				return Task.FromResult("ok");
			});

		var result = await toolbox
			.ExecuteAsync(new AiToolCall("restore_file", new Dictionary<string, string> { ["path"] = "Other.cs" }), TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		result.IsError.Should().BeTrue();
		called.Should().BeFalse();
	}

	private LocalRepoService CreateService()
	{
		var appSettings = Options.Create(new AppSettings { LocalReposRoot = _root });
		var runtimeSettings = new RuntimeSettingsService(appSettings, NullLogger<RuntimeSettingsService>.Instance);
		return new LocalRepoService(runtimeSettings, NullLogger<LocalRepoService>.Instance);
	}

	private async Task<string> InitRepoAsync(LocalRepoService service)
	{
		var path = service.GetLocalPath("o/Sample");
		Directory.CreateDirectory(path);

		foreach (var arguments in new[]
		{
			new[] { "init", "-q", "-b", "main" },
			["config", "user.email", "t@example.com"],
			["config", "user.name", "Test"],
			["config", "commit.gpgsign", "false"],
			["config", "core.autocrlf", "false"]
		})
		{
			await LocalRepoService.RunCommandAsync(path, "git", arguments, TestContext.Current.CancellationToken).ConfigureAwait(true);
		}

		await File.WriteAllTextAsync(Path.Combine(path, "tracked.txt"), "one\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
		await LocalRepoService.RunCommandAsync(path, "git", ["add", "-A"], TestContext.Current.CancellationToken).ConfigureAwait(true);
		await LocalRepoService.RunCommandAsync(path, "git", ["commit", "-q", "-m", "first"], TestContext.Current.CancellationToken).ConfigureAwait(true);

		return path;
	}

	[Fact]
	public async Task Stash_SetsEverythingAsideIncludingNewFiles_AndPopBringsItBack()
	{
		var service = CreateService();
		var path = await InitRepoAsync(service).ConfigureAwait(true);

		await File.WriteAllTextAsync(Path.Combine(path, "tracked.txt"), "changed\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
		await File.WriteAllTextAsync(Path.Combine(path, "new.txt"), "new\n", TestContext.Current.CancellationToken).ConfigureAwait(true);

		var (stashed, _) = await service.StashLocalChangesAsync("o/Sample", "test stash", TestContext.Current.CancellationToken).ConfigureAwait(true);

		stashed.Should().BeTrue();
		(await service.IsWorkingTreeCleanAsync("o/Sample", TestContext.Current.CancellationToken).ConfigureAwait(true)).Should().BeTrue();
		File.Exists(Path.Combine(path, "new.txt")).Should().BeFalse();

		var (popped, _) = await service.PopStashAsync("o/Sample", TestContext.Current.CancellationToken).ConfigureAwait(true);

		popped.Should().BeTrue();
		(await File.ReadAllTextAsync(Path.Combine(path, "tracked.txt"), TestContext.Current.CancellationToken).ConfigureAwait(true)).Should().Be("changed\n");
		File.Exists(Path.Combine(path, "new.txt")).Should().BeTrue();
	}

	[Fact]
	public async Task Stash_OnACleanTree_ReportsThereWasNothingToSetAside()
	{
		var service = CreateService();
		await InitRepoAsync(service).ConfigureAwait(true);

		var (stashed, _) = await service.StashLocalChangesAsync("o/Sample", "nothing", TestContext.Current.CancellationToken).ConfigureAwait(true);

		stashed.Should().BeFalse();
	}

	[Fact]
	public async Task RestoreFile_PutsATrackedFileBack_AndRemovesOneTheCommitDoesNotHave()
	{
		var service = CreateService();
		var path = await InitRepoAsync(service).ConfigureAwait(true);

		await File.WriteAllTextAsync(Path.Combine(path, "tracked.txt"), "changed\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
		await File.WriteAllTextAsync(Path.Combine(path, "new.txt"), "new\n", TestContext.Current.CancellationToken).ConfigureAwait(true);

		var restored = await service.RestoreFileAsync("o/Sample", "tracked.txt", TestContext.Current.CancellationToken).ConfigureAwait(true);
		var removed = await service.RestoreFileAsync("o/Sample", "new.txt", TestContext.Current.CancellationToken).ConfigureAwait(true);

		restored.Should().Contain("Restored");
		removed.Should().Contain("Removed");
		(await File.ReadAllTextAsync(Path.Combine(path, "tracked.txt"), TestContext.Current.CancellationToken).ConfigureAwait(true)).Should().Be("one\n");
		File.Exists(Path.Combine(path, "new.txt")).Should().BeFalse();
	}
}
